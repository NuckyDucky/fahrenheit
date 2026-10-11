# Fork changelog (NuckyDucky/fahrenheit)

This fork carries the Fahrenheit changes that the Shared Spira framework
(https://github.com/NuckyDucky/Shared-Spira) builds its `fh.dll` from. Every
change here is meant to go upstream to fahrenheit-crew/fahrenheit as one PR suite
once it is complete; until then Shared Spira pins a commit of this fork in its
`dependencies.lock.json`. Commits marked "(AI-assisted)" were written with
Claude and reviewed by the fork owner.

All branches below start from upstream `57d7a71` ("S0: Make arg names clearer,
split out wait-for-attach", #319). Upstream `main` has since moved on
(27 commits as of 2026-10-11, including support for the "P4" binaries of the
2026-10-01 Steam update, #330..#349); none of the fork branches is rebased onto
that yet.

## Branches

| Branch | Head | What it holds | Used by Shared Spira |
|---|---|---|---|
| `main` | `f67e5f8` | the FF X correctness suite (below) | no |
| `fix/ffx-magic-hooks` | `79ed098` | the four delegates the Shared Spira magic hooks need; a subset of the suite with Ghidra parameter names | no (superseded by the suite) |
| `feat/build-address-maps` | `33eff56` | per-build address maps so one mod build runs on more than one game build | yes: this is the pinned `fh.dll` source (`dependencies.lock.json` commit `33eff56`) |
| `fix/state-save-staging` | `c38f06a` | `feat/build-address-maps` plus atomic local-state saves | earlier pin (`c38f06a`), still built in Shared Spira's framework worktree |

## main — FF X correctness suite (2026-09-25 .. 2026-09-28)

- `41edf40` Correct 20 FF X delegate signatures. 19 generated delegates were
  labelled `StdCall`; the functions end in a plain `ret` and all 721 direct
  call sites clean the stack, so a `StdCall` hook popped the arguments twice.
  Moved to `ffx/call.cs` as `Cdecl` with traced argument types, names and
  return types; generated entries removed and addresses added to the reject
  lists. `MsActionRequest`: first argument is the acting chr, second the
  target. Five of the 20 now return a value (`MsCalcDamage` total damage,
  `MsCalcDamageCommand` hit damage, `StealRoll`, `MsDeleteInputCue` rows
  removed, `abmap_para_get_special`); `MsSetReelResult` takes two floats.
- `4171ffe` Name five unnamed delegates (`StealRoll`, `ResetBuildCue`,
  `AtelPushInt`, `MainCmdOpen`, `TkMenuMainDeactiveMenu`).
- `4353c93` Use Ghidra's names where it has one (`AtelPushStackInteger`,
  `TkMenuMainSleepMenu`).
- `d3b3cf9` `AtelScriptChunk.script_header_offsets` reads u32 (the table after
  the 0x38-byte chunk header holds one u32 per script, as FFX.exe `0x86BB10`
  does); `AtelStack.pop_float` no longer truncates F32 entries. Regression
  tests for both.
- `46b947f` `FhUtil.as_rgba` packs channels with `|` instead of `&` (it always
  returned 0); `FhEvent.invoke` returns whether handlers exist, as documented.
  Test for `as_rgba`.
- `6b551db` Keep `FUN_` names for three unnamed delegates, with comments.
- `f67e5f8` Add hit roll (`0x78A950`), crit roll (`0x789750`) and main-menu
  row (`0x8E2270`) delegates; STEP skips them because their Ghidra labels start
  with `+`.

## fix/ffx-magic-hooks (2026-09-25 .. 2026-09-28)

- `60ac000` `MsCalcDamage`, `MsCalcDamageCommand`, `MsUseChrMpLimit` as
  `Cdecl` with return values and `Chr*`/`Command*` argument types;
  `MsCheckUseCommand` filled in (chr id, command, extra MP; returns the MP cost
  or -1).
- `d0c96ef` Ghidra parameter names for the magic-hook delegates; names from the
  traced code where Ghidra has none.
- `79ed098` `action_damage_total` (int[3] HP/MP/CTB, zeroed per action at
  `0x789524`, reduced per landed hit at `0x78EDEC`) and `default_value` for
  `MsCalcDamageCommand`'s eleventh argument.

## feat/build-address-maps (2026-10-01)

Motivation: the 2026-10-01 Steam update (build 25501027) relinked `FFX.exe`;
589 of 926 code addresses the mods used moved. Instead of re-deriving every
hook, the core translates canonical addresses through a per-build map.

- `766b86a` `FhBuildMap`: the canonical builds (FFX.exe timestamp `0x55D2F3CC`,
  FFX-2.exe `0x55D433A4`) translate as identity; any other build reads
  `config/address-maps/<module>-<timestamp:X8>.map`. `ptr_at`/`calc_addr` go
  through the map; hooks check the first eight bytes of the target against the
  map's expectation; without a map, boot writes `logs/UNSUPPORTED-BUILD.txt`
  and loads no mod. New files `src/core/build_map.cs`,
  `src/core/build_map_table.cs`; small edits to `bootloader.cs`, `handles.cs`,
  `peinterop.cs`, `save_pal.cs`, `util.cs`, `runtime/cd.cs`.
- `5066cff` Map format README (`src/core/address-maps/README.md`) and tests
  (`test/core/build_map.cs`).
- `dbb54a4` A fn/sub segment may carry `-` for "no expected bytes" (the
  generator blanks them when a relocated dword sits in the first eight bytes);
  the parser used to reject the whole map.
- `33eff56` Inverse translation: `FhAddressMap.try_untranslate`,
  `FhBuildMap.untranslate`, `canonical_va` for pointers and operands read out of
  the running game and compared with canonical constants.

## fix/state-save-staging (2026-10-01, on top of feat/build-address-maps)

- `c38f06a` Local module state survives a save interrupted at any step. A save
  writes every module's state to `state.next` / `state.meta.next.json` (meta
  carries the SHA-256 of the save bytes), then the save game through a
  temporary file moved over the old one, then moves each `next` pair into
  place. On load a leftover pair whose hash matches the loaded save is
  completed, any other is discarded. `FhLocalStateInfo.SaveSha256`,
  `FhSaves.pending_save_sha256`; staged files open with `FileMode.Create`.
  Seven tests in `test/core/state.cs`.

## Still to do before the upstream PR

- Rebase all four branches onto current upstream `main` (the P4 work rewrote
  the generated `functions.*.csv` and `reject.*.rsp` data, so the suite's
  reject-list edits need redoing against those files).
- Caller-side `add esp` check for all 144 `StdCall` bindings
  (`.local/fahrenheit-upstream/caller-check.txt` in Shared Spira covers the 19
  changed here; 721 direct calls, all clean).
- Decide whether the address-map core belongs upstream or stays a Shared Spira
  extension; upstream's P4 support takes the other route (re-generated
  delegates per build).
- The fork owner opens the PR (upstream `AGENTS.md` forbids AI-opened PRs);
  mark it "(AI-assisted)".
