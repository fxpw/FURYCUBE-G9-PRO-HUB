# Proposed minimal erase scope

Updated: 2026-11-09. **Arithmetic planning only, not an installer.** No erase,
program, loader-entry or device-read operation was performed. This does not
clear the [recovery gate](RECOVERY.md).

The official updater erases `[0x2B000, 0x51000)`: 155648 bytes. Repacking its
application resource does not change that host behavior. For the small native
button fix, a future qualified sector-preserving implementation could reduce
the amount rewritten substantially.

[`tools/plan_firmware_sectors.py`](../tools/plan_firmware_sectors.py) accepts only
the pinned original resource and one of the three corrected candidate hashes.
It validates both image headers and maps differing resource bytes using the
OEM's physical destination **`0x2B00A + resource_offset`**. This is not the CPU
code-framing formula. It calculates intersecting 4 KiB sectors and produces a
JSON report on stdout. There is no transport, erase/program packet encoder or
flashable output.

| Candidate | Changed/new resource bytes | Proposed sectors | Proposed erase bytes |
|---|---:|---|---:|
| NoWait | 8 | `2B000`, `35000` | 8192 |
| OL2 | 327 | `2B000`, `35000`, `37000`, `4B000` | 16384 |
| OL3 | 714 | `2B000`, `35000`, `37000`, `4B000` | 16384 |

These exact results were computed from the corrected actual candidate files,
not merely estimated from the two-byte instruction replacement. Header CRC32,
CRC16 checkwords and new code all count. Appended FF bytes also count because
their existing device contents are unknown.

## Preservation prerequisites

The first sector contains **ten bytes before the app resource**. Their contents
and purpose are not supplied by the official update file. They must not be
replaced with zeros or guessed FF. The final proposed OLED sector also has
**1018 bytes after the original resource** without an original-file baseline.
Even bytes covered by the official file are only a reference, not a capture
of this particular mouse.

Every affected sector therefore requires two matching complete original
captures in a qualified raw representation, retained locally. A future builder
must overlay only approved changes onto those captures and preserve all other
bytes. Sector erase/program/readback, interruption behavior, an independently
reachable installed loader, and restoration of the saved sectors still need
qualification. This report provides no write order or flash commands.

This smaller application-write scope does not address side effects of entering
the loader or finishing an update. The application's update entry and the
mirrored loader's normal CRC-finish handler touch macro storage. Those effects
need their own preservation/restoration contract. Keeping BIM outside the
planned write range also does not by itself prove that USB entry survives a
bad app or interrupted sector update.

All reports retain `flashable=false`, `restore_proven=false`,
`installed_loader_compatible=false` and `transport_implemented=false`. The
stock OEM updater remains unchanged and continues to use its broader erase
range. This tool must not be mistaken for a patch to that updater.

The six synthetic test groups exercise physical offset mapping, a sector
boundary, new FF data, missing reference coverage, rejection of shrinking or
out-of-range inputs and rejection of unknown image hashes. Manufacturer
resources and actual capture data are not included in the repository.
