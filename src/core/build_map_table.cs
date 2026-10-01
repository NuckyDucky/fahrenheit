// SPDX-License-Identifier: LGPL-3.0-or-later
//
// This file is part of Fahrenheit, © 2023-2026 The Fahrenheit contributors.
// It is licensed to you under the GNU Lesser General Public License, version 3.0 or later. See COPYING, COPYING.LESSER.

namespace Fahrenheit;

/// <summary>
///     The kind of an <see cref="FhAddressSegment"/>.
/// </summary>
public enum FhAddressSegmentKind : byte {
    /// <summary>PE headers.</summary>
    hdr,
    /// <summary>A whole function body.</summary>
    fn,
    /// <summary>Part of a function whose body changed between builds.</summary>
    sub,
    /// <summary>Initialized or read-only data.</summary>
    data,
}

/// <summary>
///     One line of an address map: <c>length</c> bytes at canonical <c>old_offset</c> sit at <c>new_offset</c> in the mapped build.
///     All offsets are module-relative (VA - image base).
/// </summary>
/// <param name="expect8">
///     For <see cref="FhAddressSegmentKind.fn"/> and <see cref="FhAddressSegmentKind.sub"/>, the 8 bytes at <c>new_offset</c>
///     in the mapped build, in memory order: the first byte is the most significant byte of this value.
/// </param>
public readonly record struct FhAddressSegment(
    uint                 old_offset,
    uint                 new_offset,
    uint                 length,
    FhAddressSegmentKind kind,
    ulong                expect8,
    bool                 has_expect8);

/// <summary>
///     A parsed address map (format v1) for one module and one build. See <c>src/core/address-maps/README.md</c>.
/// </summary>
public sealed class FhAddressMap {
    public const string FORMAT_TAG = "address-map v1";

    public string                          Module             { get; }
    public uint                            BuildTimestamp     { get; }
    public uint?                           CanonicalTimestamp { get; }
    public string                          BuildSha256        { get; }
    public string                          BuildId            { get; }
    public ImmutableArray<FhAddressSegment> Segments          { get; }

    private readonly uint[]                 _old_starts;
    private readonly uint[]                 _new_starts; // segment starts in the mapped build, sorted
    private readonly int[]                  _by_new;     // _new_starts[i] belongs to Segments[_by_new[i]]
    private readonly Dictionary<uint, ulong> _expect_by_new;

    private FhAddressMap(string module, uint build_ts, uint? canonical_ts, string build_sha256, string build_id, FhAddressSegment[] segments) {
        Module             = module;
        BuildTimestamp     = build_ts;
        CanonicalTimestamp = canonical_ts;
        BuildSha256        = build_sha256;
        BuildId            = build_id;
        Segments           = ImmutableArray.Create(segments);

        _old_starts    = new uint[segments.Length];
        _new_starts    = new uint[segments.Length];
        _by_new        = new int[segments.Length];
        _expect_by_new = [];

        for (int i = 0; i < segments.Length; i++) {
            _old_starts[i] = segments[i].old_offset;
            _new_starts[i] = segments[i].new_offset;
            _by_new[i]     = i;
            if (segments[i].has_expect8) _expect_by_new.TryAdd(segments[i].new_offset, segments[i].expect8);
        }

        Array.Sort(_new_starts, _by_new);
    }

    /// <summary>
    ///     Finds the segment containing canonical <paramref name="offset"/> and returns its translation.
    /// </summary>
    /// <returns>False if the offset lies outside every segment; <paramref name="translated"/> is then the input.</returns>
    public bool try_translate(nint offset, out nint translated) {
        translated = offset;
        if (offset < 0 || (ulong)offset > uint.MaxValue) return false;

        uint o   = (uint)offset;
        int  idx = Array.BinarySearch(_old_starts, o);
        if (idx < 0) idx = ~idx - 1; // last segment whose start is <= o
        if (idx < 0) return false;

        FhAddressSegment s = Segments[idx];
        if ((ulong)o >= (ulong)s.old_offset + s.length) return false;

        translated = (nint)(s.new_offset + (o - s.old_offset));
        return true;
    }

    /// <summary>
    ///     Finds the segment containing <paramref name="translated_offset"/> in the mapped build and returns the canonical offset
    ///     it came from: the inverse of <see cref="try_translate"/>, for values the game holds in memory (table pointers,
    ///     instruction operands) that are compared with canonical constants.
    /// </summary>
    /// <returns>False if the offset lies outside every segment; <paramref name="offset"/> is then the input.</returns>
    public bool try_untranslate(nint translated_offset, out nint offset) {
        offset = translated_offset;
        if (translated_offset < 0 || (ulong)translated_offset > uint.MaxValue) return false;

        uint t   = (uint)translated_offset;
        int  idx = Array.BinarySearch(_new_starts, t);
        if (idx < 0) idx = ~idx - 1; // last segment whose mapped start is <= t
        if (idx < 0) return false;

        FhAddressSegment s = Segments[_by_new[idx]];
        if ((ulong)t >= (ulong)s.new_offset + s.length) return false;

        offset = (nint)(s.old_offset + (t - s.new_offset));
        return true;
    }

    /// <summary>
    ///     Gets the expected first 8 bytes of a <c>fn</c>/<c>sub</c> segment that starts at <paramref name="translated_offset"/>.
    /// </summary>
    public bool try_expected_bytes(nint translated_offset, out ulong expect8) {
        expect8 = 0;
        if (translated_offset < 0 || (ulong)translated_offset > uint.MaxValue) return false;
        return _expect_by_new.TryGetValue((uint)translated_offset, out expect8);
    }

    /// <summary>Formats an <c>expect8</c> value in memory order, as it is written in a map file.</summary>
    public static string format_bytes(ulong value) => value.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>
    ///     Parses address map text (format v1). On failure <paramref name="error"/> names the first problem and its line.
    /// </summary>
    public static bool try_parse(string text, [NotNullWhen(true)] out FhAddressMap? map, out string error) {
        map   = null;
        error = "";

        bool    seen_tag     = false;
        string? module       = null;
        uint?   build_ts     = null;
        uint?   canonical_ts = null;
        string  build_sha    = "";
        string  build_id     = "";

        List<FhAddressSegment> segments = [];
        string[]               lines    = text.Split('\n');

        for (int i = 0; i < lines.Length; i++) {
            int    line_no = i + 1;
            string line    = lines[i].TrimEnd('\r').Trim();

            if (line.Length == 0) continue;

            if (line[0] == '#') {
                string   body  = line[1..].Trim();
                string[] words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;

                if (body == FORMAT_TAG) {
                    seen_tag = true;
                }
                else if (words[0] == "module") {
                    if (words.Length != 2) { error = $"line {line_no}: '# module <name>' expected"; return false; }
                    module = words[1];
                }
                else if (words[0] == "build" || words[0] == "canonical") {
                    if (words.Length < 2 || !try_hex32(words[1], out uint ts)) {
                        error = $"line {line_no}: '# {words[0]} <timestamp:X8> <sha256> <buildid>' expected";
                        return false;
                    }
                    if (words[0] == "build") {
                        build_ts  = ts;
                        build_sha = words.Length > 2 ? words[2] : "";
                        build_id  = words.Length > 3 ? words[3] : "";
                    }
                    else {
                        canonical_ts = ts;
                    }
                }
                continue; // any other comment is free text
            }

            if (!seen_tag) { error = $"line {line_no}: segment before '# {FORMAT_TAG}'"; return false; }

            string[] f = line.Split((char[])[ ' ', '\t' ], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 5) { error = $"line {line_no}: 5 fields expected (old new length kind expect8), found {f.Length}"; return false; }

            if (!try_hex32(f[0], out uint old_off)) { error = $"line {line_no}: old_offset '{f[0]}' is not 8 hex digits"; return false; }
            if (!try_hex32(f[1], out uint new_off)) { error = $"line {line_no}: new_offset '{f[1]}' is not 8 hex digits"; return false; }
            if (!try_hex32(f[2], out uint length))  { error = $"line {line_no}: length '{f[2]}' is not 8 hex digits"; return false; }
            if (length == 0)                        { error = $"line {line_no}: zero length"; return false; }
            if ((ulong)old_off + length > uint.MaxValue + 1UL || (ulong)new_off + length > uint.MaxValue + 1UL) {
                error = $"line {line_no}: segment runs past 0xFFFFFFFF"; return false;
            }

            FhAddressSegmentKind kind;
            switch (f[3]) {
                case "hdr":  kind = FhAddressSegmentKind.hdr;  break;
                case "fn":   kind = FhAddressSegmentKind.fn;   break;
                case "sub":  kind = FhAddressSegmentKind.sub;  break;
                case "data": kind = FhAddressSegmentKind.data; break;
                default: error = $"line {line_no}: kind '{f[3]}' is not hdr|fn|sub|data"; return false;
            }

            ulong expect8     = 0;
            bool  code        = kind is FhAddressSegmentKind.fn or FhAddressSegmentKind.sub;
            // A code segment may carry no expected bytes ('-'): the generator blanks them where the loader relocates a
            // dword inside the first 8 bytes (ASLR rewrites it in memory), and the hook check then skips that segment.
            bool  has_expect8 = code && f[4] != "-";

            if (has_expect8) {
                if (f[4].Length != 16 || !ulong.TryParse(f[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out expect8)) {
                    error = $"line {line_no}: expect8 '{f[4]}' is not 16 hex digits or '-'"; return false;
                }
            }
            else if (f[4] != "-") {
                error = $"line {line_no}: expect8 must be '-' for kind {f[3]}"; return false;
            }

            if (segments.Count > 0) {
                FhAddressSegment prev = segments[^1];
                if (old_off <= prev.old_offset) { error = $"line {line_no}: old_offset 0x{old_off:X8} not sorted after 0x{prev.old_offset:X8}"; return false; }
                if ((ulong)prev.old_offset + prev.length > old_off) { error = $"line {line_no}: old_offset 0x{old_off:X8} overlaps the segment at 0x{prev.old_offset:X8}"; return false; }
            }

            segments.Add(new FhAddressSegment(old_off, new_off, length, kind, expect8, has_expect8));
        }

        if (!seen_tag)        { error = $"missing '# {FORMAT_TAG}'"; return false; }
        if (module is null)   { error = "missing '# module <name>'"; return false; }
        if (build_ts is null) { error = "missing '# build <timestamp:X8> <sha256> <buildid>'"; return false; }

        map = new FhAddressMap(module, build_ts.Value, canonical_ts, build_sha, build_id, [ .. segments ]);
        return true;
    }

    private static bool try_hex32(string s, out uint value) {
        value = 0;
        return s.Length == 8 && uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
