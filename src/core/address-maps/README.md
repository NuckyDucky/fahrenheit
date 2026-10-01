# Address maps

Every module offset in Fahrenheit and its mods is written for the **canonical build**, Steam build 753362
(`FFX.exe` PE timestamp `0x55D2F3CC`, `FFX-2.exe` PE timestamp `0x55D433A4`). When Steam ships a relinked
executable, its functions move. An address map tells `FhBuildMap` where each canonical offset went, so
source code never has to be renumbered.

## Where maps are read from

At start-up `FhBuildMap.initialize` reads the main module's PE header in memory. If the timestamp is not the
canonical one, it loads

    <fahrenheit>/config/address-maps/<module>-<PE TimeDateStamp:X8>.map

for example `fahrenheit/config/address-maps/FFX.exe-6AA2219C.map`. `<fahrenheit>` is the folder that holds
`bin/`, `mods/` and `logs/`. No maps ship in this folder of the repository; it only documents the format.

| State         | When                                   | Effect                                                    |
|---------------|----------------------------------------|-----------------------------------------------------------|
| `Canonical`   | canonical timestamp                    | offsets translate as identity, no lookup                  |
| `Mapped`      | a valid map for this timestamp exists  | offsets translate through the map; hooks check `expect8`  |
| `Unsupported` | no map, or the map is malformed        | `logs/UNSUPPORTED-BUILD.txt` is written, no mod is loaded |

## Format v1

UTF-8 text, LF line ends. Lines starting with `#` are comments; these header comments are read:

    # address-map v1
    # module FFX.exe
    # canonical 55D2F3CC <sha256> 753362
    # build 6AA2219C <sha256> 25501027

`address-map v1`, `module` and `build` are required; `build` and `module` must match the file being run.
Then one segment per line:

    old_offset new_offset length kind expect8
    0039B500   0039B4F0   00000018 fn  558BEC83EC105356

- `old_offset`, `new_offset`, `length`: 8 hex digits, module-relative (VA - 0x400000).
- `kind`: `hdr` (PE headers), `fn` (whole function), `sub` (part of a changed function), `data`.
- `expect8`: for `fn`/`sub`, the 16 hex digits of the 8 bytes at `new_offset` in the new file, in memory order;
  `-` for `hdr`/`data`. Before the first hook on a target that starts a `fn`/`sub` segment, Fahrenheit compares
  these bytes with memory and refuses the hook on a mismatch.
- Segments are sorted by `old_offset` and never overlap on the old side.

`translate(offset)` finds the segment containing the canonical offset and returns
`new_offset + (offset - old_offset)`. An offset outside every segment is used unchanged and logged once.

Modules other than the running game (for example an `FFX-2.exe` image mapped privately inside FFX) use
`FhBuildMap.translate_for(module, timestamp, offset)`, which loads that module's map from the same folder.
