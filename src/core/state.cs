// SPDX-License-Identifier: LGPL-3.0-or-later
//
// This file is part of Fahrenheit, © 2023-2026 The Fahrenheit contributors.
// It is licensed to you under the GNU Lesser General Public License, version 3.0 or later. See COPYING, COPYING.LESSER.

namespace Fahrenheit;

/* [fkelava 23/02/26 16:59]
 * Mods must be able to persist some information between saves or game sessions.
 *
 * They must not store such information in empty space of the save data, lest they make the save
 * impossible to load in their absence. They should also not place such data in random places on
 * the user's system where they might be hard to find, move, or delete.
 *
 * Fahrenheit provides a callback invoked on save/load, allowing mods to read from and write to an
 * isolated file per save game. This is called 'local state'. State travels with the user's Fahrenheit folder.
 *
 * Modules also have access to an analogous 'global state' file, which is not unique per save game.
 */

/* A save writes three things in this order, so an interruption at any point leaves a matching pair:
 * 1) every module's state into 'state.next' / 'state.meta.next.json', the meta carrying the SHA-256 of the save bytes;
 * 2) the save game (written to a temporary file, then moved over the old one);
 * 3) each 'next' pair moved over 'state' / 'state.meta.json'.
 * On load, a leftover 'next' pair whose hash equals the loaded save is completed (step 3 was interrupted);
 * any other leftover is discarded (step 2 never happened, so the old pair still belongs to the save).
 */

/// <summary>Implements the 'local state' mechanism of Fahrenheit.</summary>
/// <remarks>
///     In your module, implement <see cref="FhModule.save_local_state(FileStream)"/>
///     and <see cref="FhModule.load_local_state(FileStream, FhLocalStateInfo)"/>.
/// </remarks>
internal sealed class FhState {

    private const string StateName     = "state";
    private const string MetaName      = "state.meta.json";
    private const string NextStateName = "state.next";
    private const string NextMetaName  = "state.meta.next.json";

    /// <summary>
    ///     For the given save <paramref name="slot"/> and given module's <paramref name="module_context"/>,
    ///     returns the path of the local state directory and creates it.
    /// </summary>
    private string _get_state_dir_path(FhModContext mod_context, FhModuleContext module_context, int slot) {
        string local_state_dir = Path.Join(
            FhEnvironment.Finder.State.FullName,
            FhInternal.Hasher.SaveSetHash,
            FhApi.Saves.active_set,
            FhSavePal.pal_get_save_subfolder(),
            FhSavePal.pal_get_save_name_for_slot(slot),
            mod_context.Manifest.Id,
            module_context.Module.ModuleType);

        Directory.CreateDirectory(local_state_dir);
        return local_state_dir;
    }

    private static FhLocalStateInfo? _read_meta(string path) {
        if (!File.Exists(path)) return null;
        using FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<FhLocalStateInfo>(fs, FhUtil.InternalJsonOpts);
    }

    internal enum SettleResult { NONE, COMPLETED, DISCARDED }

    /// <summary>Completes or discards a 'next' pair left by an interrupted save (see the order above).</summary>
    internal static SettleResult settle_next(string state_dir, string? loaded_save_sha256) {
        string next_state = Path.Join(state_dir, NextStateName);
        string next_meta  = Path.Join(state_dir, NextMetaName);
        if (!File.Exists(next_state) && !File.Exists(next_meta)) return SettleResult.NONE;

        FhLocalStateInfo? meta = null;
        try { meta = _read_meta(next_meta); }
        catch (JsonException) { }

        bool belongs = meta?.SaveSha256 is { } sha && loaded_save_sha256 is not null
            && sha.Equals(loaded_save_sha256, StringComparison.OrdinalIgnoreCase);

        if (belongs) {
            // Order matters: state first, then meta, as in a normal commit.
            if (File.Exists(next_state)) File.Move(next_state, Path.Join(state_dir, StateName), overwrite: true);
            File.Move(next_meta, Path.Join(state_dir, MetaName), overwrite: true);
            return SettleResult.COMPLETED;
        }

        File.Delete(next_state);
        File.Delete(next_meta);
        return SettleResult.DISCARDED;
    }

    /// <summary>
    ///     Loads all modules' local state from the selected save <paramref name="slot"/>.
    ///     <paramref name="loaded_save_sha256"/> is the SHA-256 of the save game bytes that were loaded (null if unknown).
    /// </summary>
    internal void state_load_slot(int slot, string? loaded_save_sha256 = null) {
        foreach (FhModContext mod_context in FhApi.Mods.get_mods()) {
            foreach (FhModuleContext module_context in mod_context.Modules) {
                string module_type   = module_context.Module.ModuleType;
                string state_dir     = _get_state_dir_path(mod_context, module_context, slot);

                switch (settle_next(state_dir, loaded_save_sha256)) {
                    case SettleResult.COMPLETED: FhInternal.Log.Warning($"{module_type}: completed local state of an interrupted save."); break;
                    case SettleResult.DISCARDED: FhInternal.Log.Warning($"{module_type}: discarded local state staged for a save that was never written."); break;
                }

                string state_fn      = Path.Join(state_dir, StateName);
                string state_meta_fn = Path.Join(state_dir, MetaName);

                if (!File.Exists(state_fn)) continue;

                using FileStream state_meta_fs = File.Open(state_meta_fn, FileMode.Open,         FileAccess.Read, FileShare.None);
                using FileStream state_fs      = File.Open(state_fn,      FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);

                FhInternal.Log.Debug($"Reading metadata for module {module_type}.");
                FhLocalStateInfo state_meta = JsonSerializer.Deserialize<FhLocalStateInfo>(state_meta_fs, FhUtil.InternalJsonOpts)
                    ?? throw new Exception("FH_E_LOCAL_STATE_META_BLOCK_NULL");

                if (state_meta.SaveSha256 is { } sha && loaded_save_sha256 is not null
                    && !sha.Equals(loaded_save_sha256, StringComparison.OrdinalIgnoreCase))
                    FhInternal.Log.Warning($"{module_type}: local state was written with different save bytes than the save loaded.");

                FhInternal.Log.Debug($"{state_fn} -> {module_type}.");
                module_context.Module.load_local_state(state_fs, state_meta);
            }
        }
    }

    /// <summary>
    ///     Step 1 of a save: writes all modules' local state for <paramref name="slot"/> into the 'next' pair,
    ///     recording <paramref name="save_sha256"/>, the SHA-256 of the save bytes about to be written.
    /// </summary>
    internal void state_stage_slot(int slot, string save_sha256) {
        FhApi.Saves.pending_save_sha256 = save_sha256;
        try {
            foreach (FhModContext mod_context in FhApi.Mods.get_mods()) {
                foreach (FhModuleContext module_context in mod_context.Modules) {
                    string module_type   = module_context.Module.ModuleType;
                    string state_dir     = _get_state_dir_path(mod_context, module_context, slot);

                    string state_fn      = Path.Join(state_dir, NextStateName);
                    string state_meta_fn = Path.Join(state_dir, NextMetaName);

                    FhLocalStateInfo state_meta = new(mod_context.Manifest.Version, save_sha256);

                    // FileMode.Create truncates: a shorter state must not keep the tail of a longer one.
                    using (FileStream state_fs = File.Open(state_fn, FileMode.Create, FileAccess.ReadWrite, FileShare.Read)) {
                        FhInternal.Log.Debug($"{module_type} -> {state_fn}.");
                        module_context.Module.save_local_state(state_fs);
                        state_fs.Flush(flushToDisk: true);
                    }

                    using (FileStream state_meta_fs = File.Open(state_meta_fn, FileMode.Create, FileAccess.Write, FileShare.None)) {
                        FhInternal.Log.Debug($"Writing metadata for module {module_type}.");
                        state_meta_fs.Write(JsonSerializer.SerializeToUtf8Bytes(state_meta, FhUtil.InternalJsonOpts));
                        state_meta_fs.Flush(flushToDisk: true);
                    }
                }
            }
        }
        finally {
            FhApi.Saves.pending_save_sha256 = null;
        }
    }

    /// <summary>Step 3 of a save: moves every module's 'next' pair for <paramref name="slot"/> into place.</summary>
    internal void state_commit_slot(int slot) {
        foreach (FhModContext mod_context in FhApi.Mods.get_mods()) {
            foreach (FhModuleContext module_context in mod_context.Modules) {
                string state_dir  = _get_state_dir_path(mod_context, module_context, slot);
                string next_state = Path.Join(state_dir, NextStateName);
                string next_meta  = Path.Join(state_dir, NextMetaName);

                if (File.Exists(next_state)) File.Move(next_state, Path.Join(state_dir, StateName), overwrite: true);
                if (File.Exists(next_meta))  File.Move(next_meta,  Path.Join(state_dir, MetaName),  overwrite: true);
            }
        }
    }

    /// <summary>
    ///     Writes a save game and all modules' local state for <paramref name="slot"/> in the order described above.
    ///     <paramref name="save"/> must already carry its CRC.
    /// </summary>
    internal void save_slot_with_state(int slot, string save_path, ReadOnlySpan<byte> save) {
        string sha = Convert.ToHexString(SHA256.HashData(save));

        state_stage_slot(slot, sha);

        string temp_path = save_path + ".tmp";
        using (FileStream save_stream = File.Open(temp_path, FileMode.Create, FileAccess.Write, FileShare.None)) {
            save_stream.Write(save);
            save_stream.Flush(flushToDisk: true);
        }
        File.Move(temp_path, save_path, overwrite: true);

        state_commit_slot(slot);
    }
}
