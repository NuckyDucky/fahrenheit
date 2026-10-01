// SPDX-License-Identifier: LGPL-3.0-or-later
//
// This file is part of Fahrenheit, © 2023-2026 The Fahrenheit contributors.
// It is licensed to you under the GNU Lesser General Public License, version 3.0 or later. See COPYING, COPYING.LESSER.

namespace Fahrenheit;

/// <summary>
///     How the running game build relates to the canonical build every address in Fahrenheit and its mods is written for.
/// </summary>
public enum FhBuildState {
    /// <summary>The canonical build. Offsets translate as identity.</summary>
    Canonical,
    /// <summary>A different build with an address map. Offsets translate through the map.</summary>
    Mapped,
    /// <summary>A different build without a usable address map. No mod is loaded.</summary>
    Unsupported,
}

/// <summary>
///     Translates canonical-build module offsets to the running build through per-build address maps.
/// </summary>
/// <remarks>
///     The canonical build is Steam build 753362. Every offset in source is a canonical offset. A map is read from
///     <c>&lt;fahrenheit&gt;/config/address-maps/&lt;module&gt;-&lt;PE TimeDateStamp:X8&gt;.map</c>.
///     See <c>src/core/address-maps/README.md</c> for the format.
/// </remarks>
public static unsafe class FhBuildMap {
    public const string FFX_MODULE   = "FFX.exe";
    public const string FFX2_MODULE  = "FFX-2.exe";
    public const string MAP_DIR_NAME = "address-maps";
    public const string UNSUPPORTED_REPORT_NAME = "UNSUPPORTED-BUILD.txt";

    /// <summary>Preferred image base of FFX.exe and FFX-2.exe; a map offset is a canonical VA minus this.</summary>
    public const nint CANONICAL_IMAGE_BASE = 0x400000;

    /// <summary>FFX.exe of Steam build 753362 (SHA-256 3d13e5ed…f277e1).</summary>
    public const uint FFX_CANONICAL_TIMESTAMP  = 0x55D2F3CC;

    /// <summary>
    ///     FFX-2.exe of Steam build 753362 (SHA-256 d7f08cda…3251ef20). Read from the PE header of the archived
    ///     canonical file (.local/game-build-753362/FFX-2.exe in the overhaul repo) on 2026-10-01.
    /// </summary>
    public const uint FFX2_CANONICAL_TIMESTAMP = 0x55D433A4;

    private const int UNMAPPED_LOG_CAP = 256;

    // Hot path: true while every main-module offset translates as identity (Canonical, Unsupported, or not yet initialized).
    private static bool          s_identity = true;
    private static FhAddressMap? s_main_map;
    private static string        s_map_dir  = "";

    private static readonly Lock                                        s_lock     = new();
    private static readonly Dictionary<(string, uint), FhAddressMap?>   s_maps     = [];
    private static readonly HashSet<(string, nint)>                     s_unmapped = [];
    private static          List<(FhLogLevel level, string message)>?   s_pending  = [];

    /// <summary>The state of the running game's main module.</summary>
    public static FhBuildState State               { get; private set; } = FhBuildState.Canonical;
    public static bool         is_supported        => State != FhBuildState.Unsupported;
    public static string       MainModule          { get; private set; } = "";
    public static uint         Timestamp           { get; private set; }
    public static uint         SizeOfImage         { get; private set; }
    public static string       Sha256              { get; private set; } = "";
    public static string       ExpectedMapFileName { get; private set; } = "";
    public static string?      MapPath             { get; private set; }
    public static int          SegmentCount        => s_main_map?.Segments.Length ?? 0;

    /// <summary>The canonical PE timestamp of a game module, or null for any other module.</summary>
    public static uint? canonical_timestamp_of(string module) {
        if (module.Equals(FFX_MODULE,  StringComparison.OrdinalIgnoreCase)) return FFX_CANONICAL_TIMESTAMP;
        if (module.Equals(FFX2_MODULE, StringComparison.OrdinalIgnoreCase)) return FFX2_CANONICAL_TIMESTAMP;
        return null;
    }

    /// <summary>The file name a map for <paramref name="module"/> at <paramref name="timestamp"/> must have.</summary>
    public static string map_file_name(string module, uint timestamp) => $"{module}-{timestamp:X8}.map";

    /// <summary>
    ///     Decides the state of <paramref name="module"/> at <paramref name="timestamp"/>, loading its map from
    ///     <paramref name="map_dir"/> when it is not canonical. A missing or malformed map gives <see cref="FhBuildState.Unsupported"/>.
    /// </summary>
    public static FhBuildState resolve(string module, uint timestamp, string map_dir,
                                       out FhAddressMap? map, out string map_path, out string error) {
        map      = null;
        error    = "";
        map_path = Path.Join(map_dir, map_file_name(module, timestamp));

        uint? canonical = canonical_timestamp_of(module);
        if (canonical == timestamp) return FhBuildState.Canonical;

        if (!File.Exists(map_path)) {
            error = "no map file";
            return FhBuildState.Unsupported;
        }

        string text;
        try {
            text = File.ReadAllText(map_path, Encoding.UTF8);
        }
        catch (Exception e) {
            error = $"cannot read map: {e.Message}";
            return FhBuildState.Unsupported;
        }

        if (!FhAddressMap.try_parse(text, out FhAddressMap? parsed, out string parse_error)) {
            error = $"malformed map: {parse_error}";
            return FhBuildState.Unsupported;
        }
        if (!parsed.Module.Equals(module, StringComparison.OrdinalIgnoreCase)) {
            error = $"malformed map: '# module {parsed.Module}' does not match {module}";
            return FhBuildState.Unsupported;
        }
        if (parsed.BuildTimestamp != timestamp) {
            error = $"malformed map: '# build {parsed.BuildTimestamp:X8}' does not match {timestamp:X8}";
            return FhBuildState.Unsupported;
        }
        if (canonical is not null && parsed.CanonicalTimestamp is not null && parsed.CanonicalTimestamp != canonical) {
            error = $"malformed map: '# canonical {parsed.CanonicalTimestamp:X8}' is not the canonical build {canonical:X8}";
            return FhBuildState.Unsupported;
        }

        map = parsed;
        return FhBuildState.Mapped;
    }

    /// <summary>
    ///     Reads the main module's PE header and decides <see cref="State"/>. Called once from <see cref="FhEnvironment"/>'s
    ///     static constructor right after <see cref="FhEnvironment.BaseAddr"/> is known.
    /// </summary>
    internal static void initialize() {
        nint base_addr = FhEnvironment.BaseAddr;
        s_map_dir = Path.Join(FhEnvironment.Finder.Config.FullName, MAP_DIR_NAME);

        string process_path = Environment.ProcessPath ?? "";
        MainModule = Path.GetFileName(process_path);

        byte* image   = (byte*)base_addr;
        int   lfanew  = *(int*)(image + 0x3C);
        Timestamp     = *(uint*)(image + lfanew + 8);       // IMAGE_FILE_HEADER.TimeDateStamp
        SizeOfImage   = *(uint*)(image + lfanew + 24 + 56); // IMAGE_OPTIONAL_HEADER.SizeOfImage (same offset in PE32 and PE32+)

        try {
            using FileStream exe = File.OpenRead(process_path);
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(exe));
        }
        catch (Exception e) {
            Sha256 = $"<unreadable: {e.Message}>";
        }

        ExpectedMapFileName = map_file_name(MainModule, Timestamp);

        FhBuildState state = resolve(MainModule, Timestamp, s_map_dir, out FhAddressMap? map, out string map_path, out string error);

        State      = state;
        s_main_map = map;
        MapPath    = state == FhBuildState.Canonical ? null : map_path;
        lock (s_lock) s_maps[(MainModule.ToUpperInvariant(), Timestamp)] = map;
        s_identity = state != FhBuildState.Mapped;

        string line = $"build: module {MainModule}, timestamp 0x{Timestamp:X8}, SizeOfImage 0x{SizeOfImage:X}, sha256 {Sha256}, "
                    + $"state {state}, map {(MapPath ?? "-")}, segments {SegmentCount}"
                    + (error.Length > 0 ? $" ({error})" : "");

        log(state == FhBuildState.Unsupported ? FhLogLevel.Error : FhLogLevel.Info, line);
    }

    /// <summary>Translates a canonical offset in the running game's main module (FFX.exe or FFX-2.exe).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint translate(nint offset) => s_identity ? offset : translate_main(offset);

    /// <summary>
    ///     Translates a canonical offset in <paramref name="module"/>. Only the running game's main module is mapped;
    ///     any other module is returned unchanged.
    /// </summary>
    public static nint translate(string module, nint offset) {
        if (s_identity) return offset;
        if (!module.Equals(MainModule, StringComparison.OrdinalIgnoreCase)) return offset;
        return translate_main(offset);
    }

    /// <summary>
    ///     Maps an offset of the running game's main module back to the canonical build: the inverse of
    ///     <see cref="translate(nint)"/>, for values the game holds in memory (table pointers, instruction operands)
    ///     that a mod compares with canonical constants. An offset outside every segment is returned unchanged.
    /// </summary>
    public static nint untranslate(nint translated_offset) => s_identity ? translated_offset : untranslate_main(translated_offset);

    /// <summary>
    ///     Maps an absolute address inside the running game's main module to the VA it has in the canonical build, at the
    ///     canonical preferred image base <see cref="CANONICAL_IMAGE_BASE"/> (every map offset is VA - that base).
    ///     An address outside the module is returned unchanged.
    /// </summary>
    public static nint canonical_va(nint address) {
        nint offset = address - FhEnvironment.BaseAddr;
        if (offset < 0 || offset >= (nint)SizeOfImage) return address;
        return untranslate(offset) + CANONICAL_IMAGE_BASE;
    }

    /// <summary>
    ///     Translates a canonical offset in <paramref name="module"/> for the build with PE <paramref name="timestamp"/>, which
    ///     need not be the running game (e.g. the FFX-2.exe image mapped privately inside FFX). Loads the map on first use.
    /// </summary>
    public static nint translate_for(string module, uint timestamp, nint offset) {
        if (canonical_timestamp_of(module) == timestamp) return offset;

        FhAddressMap? map = get_map(module, timestamp);
        if (map is not null && map.try_translate(offset, out nint translated)) return translated;

        note_unmapped(module, offset);
        return offset;
    }

    /// <summary>Loads (once) the map for <paramref name="module"/> at <paramref name="timestamp"/> and returns its state.</summary>
    public static FhBuildState load(string module, uint timestamp) {
        if (canonical_timestamp_of(module) == timestamp) return FhBuildState.Canonical;
        return get_map(module, timestamp) is null ? FhBuildState.Unsupported : FhBuildState.Mapped;
    }

    /// <summary>
    ///     Gets the first 8 bytes the map expects at <paramref name="translated_offset"/> in the running main module,
    ///     when a <c>fn</c> or <c>sub</c> segment starts there. Value is in memory order (first byte most significant).
    /// </summary>
    public static bool try_expected_bytes(nint translated_offset, out ulong expect8) {
        expect8 = 0;
        return State == FhBuildState.Mapped && s_main_map is not null && s_main_map.try_expected_bytes(translated_offset, out expect8);
    }

    /// <summary>
    ///     Checks the 8 bytes at absolute <paramref name="ptr_target"/> against the map before a hook is created over it.
    /// </summary>
    /// <returns>False only when the map has expected bytes for this target and memory differs.</returns>
    internal static bool verify_hook_target(nint ptr_target, string hook_name) {
        if (s_identity) return true;

        nint offset = ptr_target - FhEnvironment.BaseAddr;
        if (offset < 0 || offset >= (nint)SizeOfImage) return true;
        if (!try_expected_bytes(offset, out ulong expect8)) return true;

        ulong found = BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>((void*)ptr_target, 8));
        if (found == expect8) return true;

        log(FhLogLevel.Error, $"(0x{ptr_target:X}) refused: expected {FhAddressMap.format_bytes(expect8)} found {FhAddressMap.format_bytes(found)} ({hook_name})");
        return false;
    }

    /// <summary>
    ///     Writes start-up lines queued before the logger existed. Called first thing in <see cref="FhEnvironment.boot"/>.
    /// </summary>
    internal static void flush_log() {
        List<(FhLogLevel level, string message)>? pending;
        lock (s_lock) {
            pending   = s_pending;
            s_pending = null;
        }
        if (pending is null) return;
        foreach ((FhLogLevel level, string message) in pending) FhInternal.Log.Log(level, message);
    }

    /// <summary>
    ///     Writes <c>logs/UNSUPPORTED-BUILD.txt</c> when the build is unsupported, or removes a stale one otherwise.
    /// </summary>
    /// <returns>True if the build is unsupported and mods must not load.</returns>
    internal static bool handle_unsupported(DirectoryInfo logs_root) {
        string report_path = Path.Join(logs_root.FullName, UNSUPPORTED_REPORT_NAME);

        if (State != FhBuildState.Unsupported) {
            try { if (File.Exists(report_path)) File.Delete(report_path); } catch { /* best effort */ }
            return false;
        }

        string report = string.Join("\n",
            $"module: {MainModule}",
            $"timestamp: 0x{Timestamp:X8}",
            $"SizeOfImage: 0x{SizeOfImage:X}",
            $"sha256: {Sha256}",
            $"expected map file: {MAP_DIR_NAME}/{ExpectedMapFileName} (in the Fahrenheit config folder)",
            "",
            "This game build is not supported yet: the game runs without mods until an address map for this build ships.",
            "");

        try {
            File.WriteAllText(report_path, report, new UTF8Encoding(false));
        }
        catch (Exception e) {
            FhInternal.Log.Error($"build: could not write {report_path}: {e.Message}");
        }

        FhInternal.Log.Error($"build: UNSUPPORTED - {MainModule} 0x{Timestamp:X8} SizeOfImage 0x{SizeOfImage:X} sha256 {Sha256}; "
                           + $"expected {MAP_DIR_NAME}/{ExpectedMapFileName}; no mods loaded; report at {report_path}");
        return true;
    }

    private static nint translate_main(nint offset) {
        FhAddressMap? map = s_main_map;
        if (map is not null && map.try_translate(offset, out nint translated)) return translated;

        note_unmapped(MainModule, offset);
        return offset;
    }

    private static nint untranslate_main(nint translated_offset) {
        FhAddressMap? map = s_main_map;
        if (map is not null && map.try_untranslate(translated_offset, out nint offset)) return offset;

        note_unmapped(MainModule, translated_offset, reverse: true);
        return translated_offset;
    }

    private static FhAddressMap? get_map(string module, uint timestamp) {
        (string, uint) key = (module.ToUpperInvariant(), timestamp);

        lock (s_lock) {
            if (s_maps.TryGetValue(key, out FhAddressMap? cached)) return cached;
        }

        string map_dir = s_map_dir.Length > 0 ? s_map_dir : Path.Join(FhEnvironment.Finder.Config.FullName, MAP_DIR_NAME);
        FhBuildState state = resolve(module, timestamp, map_dir, out FhAddressMap? map, out string map_path, out string error);

        lock (s_lock) s_maps[key] = map;

        log(state == FhBuildState.Mapped ? FhLogLevel.Info : FhLogLevel.Error,
            $"build: module {module}, timestamp 0x{timestamp:X8}, state {state}, map {map_path}, segments {map?.Segments.Length ?? 0}"
            + (error.Length > 0 ? $" ({error})" : ""));
        return map;
    }

    private static void note_unmapped(string module, nint offset, bool reverse = false) {
        lock (s_lock) {
            if (s_unmapped.Count > UNMAPPED_LOG_CAP) return;
            if (s_unmapped.Count == UNMAPPED_LOG_CAP) {
                s_unmapped.Add((string.Empty, -1));
                log_locked(FhLogLevel.Warning, $"build: more than {UNMAPPED_LOG_CAP} unmapped offsets; no further ones are logged");
                return;
            }
            if (!s_unmapped.Add((module.ToUpperInvariant(), reverse ? -offset - 1 : offset))) return;
            log_locked(FhLogLevel.Warning, reverse
                ? $"build: {module}+0x{offset:X} (an offset of the running build) is outside every map segment; used unchanged"
                : $"build: {module}+0x{offset:X} is outside every map segment; used unchanged");
        }
    }

    private static void log(FhLogLevel level, string message) {
        lock (s_lock) {
            if (s_pending is not null) {
                s_pending.Add((level, message));
                return;
            }
        }
        FhInternal.Log.Log(level, message);
    }

    // s_lock held by caller.
    private static void log_locked(FhLogLevel level, string message) {
        if (s_pending is not null) {
            s_pending.Add((level, message));
            return;
        }
        FhInternal.Log.Log(level, message);
    }
}
