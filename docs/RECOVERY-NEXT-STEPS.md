# Recovery follow-up: exact readers, identity and the next safe evidence

Updated: 2026-11-09. **There is still no complete dump of this mouse and no
proven app-independent full restore. Firmware installation remains blocked.**
The user reports that the bottom has **no FCC ID**; do not ask for that label
again or treat an indexed FCC filing as a match to the user's unit.

**Subsequent hardware evidence:** the user withdrew the first two reference
photos as another mouse and supplied five actual-unit photos plus transcription.
They identify `BK3633QN48`, `EPC-V18U0002`, `2025-4-3`, `PAW3311DB-T2MU` and `A3311`.
The BK3533/BK3633 identity question below is now resolved for that photographed
unit; the old search record is retained as history, not a request for more FCC
or chip-name evidence. Board routing, safe entry, protection and full restoration
remain unproven. See [updated hardware identity](HARDWARE-IDENTITY.md).

This pass used existing files, static PE/firmware inspection, primary web
sources and isolated original-function emulation with all I/O replaced. It did
not launch any vendor executable, open a programming connection,
send a device command, enter a loader, reset a device, attach a debugger, change
a driver, or erase/program memory. The limits already established in
[RECOVERY.md](RECOVERY.md), [RECOVERY-UART.md](RECOVERY-UART.md),
[HARDWARE-IDENTITY.md](HARDWARE-IDENTITY.md) and
[USB-READBACK.md](USB-READBACK.md) still apply.

## New concrete findings

### 1. The stock descriptor does not identify the silicon or this exact unit

The original normalized 1.08 application contains the following standard USB
device descriptor at CPU address `0x449C2`:

```text
12 01 10 01 00 00 00 40 57 1D 48 21 08 01 01 02 00 01
```

It declares VID/PID `1D57:2148`, device revision `0x0108`, manufacturer/product
string indices 1/2, and **serial-number string index 0**. The nearby string
descriptors contain `G9 PRO` at `0x449A8` and `Beken` at `0x449B6`. There is no
ASCII or UTF-16LE `BK3533` or `BK3633` string in the normalized application.

These are facts about the pinned downloaded image, not a newly acquired live
descriptor. They explain a limit of software identification: the USB names and
version can support product-family matching, but supply neither a silicon part
number nor a unique device serial. A Windows instance/path or physical USB port
does not fill that gap. No new USB selector is proposed to obtain such an ID.

The manufacturer still advertises BK3533 on its
[G9 product page](https://www.furycube.com/products/g9-ultra-light-wireless-gaming-mouse).
The inspected [Beken product page](https://www.bekencorp.com/index/goods/detail/cid/43.html)
is explicitly for BK3633. Targeted searches of Beken's public product/docs
domains and its pinned SDK did not obtain a BK3533 datasheet or an official
BK3533-to-BK3633 equivalence statement. This is a bounded search result, not a
claim that BK3533 cannot exist or that the marketing name is a typo.

The pinned SDK tree contains 4,633 files and no `3533` path. This is useful for
qualifying the source, not proof of the chip installed in the mouse. Likewise,
`get_flash_ID`/`flash_mid` in the
[SDK flash driver](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/platform/mcu/bk3633/beken/driver/flash/flash.c)
select a flash-device configuration table; a flash manufacturer/device ID must
not be substituted for the mouse MCU/package identity.

### 2. The official SDK HID tool has a real dump workflow, with preparation

The previously downloaded
[official SDK download-tools archive](https://github.com/bekencorp/bk3633_mesh_sdk/tree/70d28da4d0c97d45a119644d9eb9abd0db459b14/download_tools)
contains `HID Flash Download Tool_V2.5.5.exe`. Its SHA256 is:

```text
9240adf66b69d0e61bc6f87089bac441d042bc97c9a159401c174f790b447e0c
```

Static inspection adds more than a generic tool-name lead:

| Location in that original x86 PE | Finding | Consequence |
|---|---|---|
| `0x606018`, GUI setup `0x40CD0E` | `Dump FLASH` control | A dump UI really exists; this is not the G9 OEM updater's CRC-verification reply |
| `0x4058C0..0x4059B0` | Dump wrapper calls a selected preparation function before requesting/receiving data | The dump workflow is not just a passive input read |
| `0x41E060`, table `0x608FB0` | Selects that preparation function by tool chip index | A correct chip/protocol selection must be established first |
| Saved `Tool Setting.ini`, index 17 | Preparation table entry is `0x417000` | This is an archive's saved selection, **not identification of the G9** |
| `0x417054`, `0x417082` | Preparation emits messages beginning `0x0D` and `04 D2`, with a repeated exchange, and calls additional initialization | These effects are not qualified as non-mutating or compatible with the G9 loader; do not run the dump button as a probe |
| `0x4057D0..0x40588B` | Receives data in chunks of at most 64 bytes and writes it to the chosen output stream | A concrete data-dump implementation exists to audit further offline |

The register-operation tables at `0x609190`/`0x609208` are a different path:
their slot-17 functions `0x4169A0`/`0x4169E0` operate on SPI register transactions.
They must not be mislabelled as a full-flash backup function merely because
one table entry performs a read.

No matching `A745:0033` identity/protocol relationship to the current OEM G9
loader was established in this pass. The SDK tool's preparation, selected chip
index, addressing, security mode and output representation still need proof.
Its existence does **not** authorize entering the G9 loader, whose entry already
has the macro-sector mutation risk described in RECOVERY.md.

Local reproduction: ignored `evidence/recovery-uart/audit-hid-dump.py` reads PE
bytes and disassembles selected instructions without loading the executable.
It checks the pinned preparation-table entry and the two message-construction
instructions. The independent source bytes and addresses above permit the same
check with any static PE disassembler. No vendor program was executed.

### 3. BKFIL offers bounded reads; selecting an app file is not a full backup

Quectel's primary distribution page explicitly distinguishes reading a region
defined by a selected firmware/configuration file from erasing and downloading.
Its configuration also includes independent full erase, OTP and SecureBoot
operations. Those are not implicit prerequisites to enable on a reader.
[Quectel BKFIL documentation](https://developer.quectel.com/doc/shortrange/FGM842D/en/Resources/Tools/bkfil.html).

In the pinned `bk_loader.exe` 2.1.12.1 already recorded in RECOVERY-UART.md,
static inspection found a whole-flash routine `0x418AE0..0x418C9F`: it derives
the number of 4 KiB reads from a flash-configuration table and sends them through
`0x4142B0`. A surrounding read workflow at `0x41B7C0..0x41BAAE` can call it;
another branch performs selected-region reads. This is a more specific future
audit target than blindly running `read --help` or the GUI.

The same executable contains separate unprotect paths, including
`0x419130..0x41917E` and `0x41BBB4..0x41BBEB`. Their presence does not prove that
every read invokes them, and the local read loop alone does not prove that all
connection/selection paths exclude them. An end-to-end call-path audit must
settle this for the exact selected chip and configuration before attaching it.
No protection-changing operation is acceptable as a prerequisite to this backup.

The exact BK3633-recommended version remains BKFIL 2.1.10.0, as documented in
the primary [Tuya BK3633 platform guide](https://developer.tuya.com/en/docs/iot-device-dev/bluetooth_platform_ble_bk3633?id=Kdi0in7x1sbwe).
The newer binary and the older SDK tools are research artifacts, not silently
interchangeable G9 readers. The original app resource does not specify the full
installed flash capacity or include the missing boot/stack/device data.

## Confirmed offline UART page contract and its limitations

The original `bk_loader.exe` was **not launched**. The scratch harness
`evidence/recovery-uart/emulate-bkfil-read.py` loads PE bytes into Unicorn and
enters only the two selected functions. Serial submission/completion, logging,
CRT and file operations are local test doubles; an unknown control transfer
fails the test. Its source SHA is pinned to the value in RECOVERY-UART.md.

Executing original instructions at `0x4142B0..0x4144C6` confirmed:

| Item | Reconstructed bytes/behavior |
|---|---|
| UART HCI read-page request, 12 bytes | `01 E0 FC FF F4 05 00 09` followed by the aligned address as uint32 little-endian |
| Expected reply length | Exactly 4,111 bytes |
| Fixed reply prefix, bytes 0..9 | `04 0E FF 01 E0 FC F4 06 10 09` |
| Byte 10 | Unchecked by the native page function; exact hardware status semantics remain unqualified |
| Bytes 11..14 | The requested page address, uint32 little-endian |
| Bytes 15..4110 | 4,096 returned data bytes |
| Alignment | Native code silently rounds an input address down to a 4 KiB boundary; our codec rejects misalignment instead |

This `0x09` is a **UART HCI subcommand**, not the normal mouse's HID `0x09`
macro feature. The contract is not a reason to send it to a mouse HID endpoint.

The eight page-function cases passed: normal data and pending-then-ready were
accepted; wrong header, stale address and completion error were rejected;
nonzero byte 10 and a changed payload byte were accepted by that local native
function; an unaligned address was rounded down. This demonstrates missing local
validation, not that a real device uses nonzero status or that all lower
transport layers lack error detection.

Three more original whole-read cases used a table fixture for flash ID
`0x13311C`, whose table capacity is 524,288 bytes. **This is a test fixture, not
the G9's established flash ID/capacity.** The function issued 128 page reads.
With `fopen_s` failing and returning a NULL file pointer, the function still
returned true. It also returned true with `fwrite` returning zero. The relevant
branches are `0x418C34..0x418C5B`; exact file-write completion is not checked.
Therefore a positive result from that function cannot by itself prove a saved
backup. The 11-case result is recorded in ignored
`evidence/recovery-uart/bkfil-read-offline-results.json`.

### Preconditions before that page loop: not yet a safe connection

RTTI identifies the vtable at `0x4CA500` as `CBK_FLASH_COMMON`; its read member
at `0x4CA508` is `0x41B7C0`. The read thread first calls connection/selection
routine `0x4A8800`, then dispatches the selected read member at
`0x4AF123..0x4AF12E`. The common class is constructed at `0x4A8C1C..0x4A8C4C`
under a configuration/identification branch. Its presence and a `BK3633` name
in another registry are not a proven exact-chip match.

For the full-read branch, the surrounding workflow requires a nonzero full-read
selection at state offset `0x10B48`, a recognized flash ID in the 72-entry table
at `0x4C84F0`, allocated destination storage, and no cancellation. The capacity
comes from that table's bit count divided by eight; it is not inferred from the
G9 application file. A different branch reads configured regions, which cannot
be labelled full-chip without checking coverage. An additional NVR path is
conditional and is not established as a G9-readable unique-data region.

More importantly, a concrete pre-read side path was found: connection code
`0x4A916D..0x4A918E` checks flash ID `0x1370CD` and calls `0x4535D0`, then can
substitute reported ID `0x1360CD`. That routine issues SPI transactions including
`0x06` and vendor `0xEF` exchanges (`0x4536E0..0x45372F`), plus other vendor
operations. Their persistent effects have not been qualified for this mouse.
The path precedes read dispatch. Consequently it is not sufficient to see a
read-only page command and assume the surrounding reader has no state-changing
preparation. No connection/auto-detection/flash-repair path from this tool is
enabled in the project.

## Implemented own offline codec and archive verifier

[`tools/g9_backup_archive.py`](../tools/g9_backup_archive.py) uses only the Python
standard library. It has no serial/USB opening, subprocess, loader transition,
auto-probe, protection, erase or device-write capability. It provides:

* A pure aligned read-page encoder and exact-length/header/address reply parser.
  Byte 10 must be zero as a conservative rejection policy; this does not claim
  that its hardware semantics or G9 compatibility have been validated.
* A captured-page schema requiring an **explicit declared capacity**, capacity
  evidence, device-identity evidence and representation evidence. It contains
  no default G9 capacity. The 16 MiB/32 MiB implementation resource limits are
  not assertions about the chip.
* Complete page coverage from address zero through the declared capacity, with
  no holes, duplicates, overlaps, out-of-range pages or truncated replies.
* Two distinct declared capture IDs/times, agreeing identity/capacity/format,
  and byte-identical complete payloads. Metadata alone cannot prove that two
  independent physical reads really occurred.
* Unique output directories; exact file-write/readback checks; full-file and
  per-page SHA256; original input captures and page index preservation. The
  manifest is written last, so incomplete output cannot be reported as ready.
  Empty files, duplicate JSON members and partial coverage are rejected. Inputs
  must be existing regular files; Windows device/COM aliases and UNC device or
  network paths are rejected before opening.

The manifest deliberately retains `physicalBackupVerified=false`,
`independentAcquisitionVerified=false`, `appIndependentEntryVerified=false`,
`restorationVerified=false`, and `recoveryGate=BLOCKED`. Its positive statement
is only `COMPLETE_FOR_DECLARED_CAPACITY` plus matching captured bytes and saved
archive files. It cannot certify the true flash capacity, encrypted/raw layout,
protected NVM or independent recovery. Real device images must stay outside the
public source repository.

Offline checks, which do not access hardware:

```text
python -m unittest discover -s tests -p test_backup_archive.py -v
python tools/g9_backup_archive.py encode-page --address 0x12000
```

The nine test groups pass, covering the original-instruction packet fixture,
strict reply checks, explicit synthetic-fixture opt-in, holes/duplicates/range
errors, conflicting captures, file/hash verification, save failure, forbidden
device paths and recovery-gate labels.
No physical G9 capture has been passed to this UART decoder. The synthetic
8 KiB test data is explicitly labelled a fixture and establishes no G9 capacity.

The `archive` CLI takes two **already captured** schema files and an explicit
local output parent. It cannot acquire them. Passing `--allow-fixture` is
required for synthetic input, and does not change the blocked recovery flags.

## What can be automated safely now

| Work | Can proceed without touching the device? | What it establishes |
|---|---|---|
| Pin hashes and inventory SDK/tool archives, PE tables and original descriptors | Yes | Reproducible source/tool evidence; not physical chip identification |
| Follow the exact HID dump preparation and BKFIL selected-chip read call graph; emulate encoders with serial/USB calls replaced by local sinks | Yes | Candidate protocol, bounds and possible side effects; not a live verified dump |
| Verify/copy existing settings archives and build a coverage manifest | Yes | Preservation of already captured settings only |
| Verify captured HCI pages and compare two supplied complete passes using the new offline tool | Yes, for this reconstructed representation | Complete matching input coverage and saved files; physical capacity/raw/encryption/CRC and entry/restore still need independent proof |
| Automatically try chip profiles, unknown HID IDs, loader entry or programmer connection | **No** | Still violates the present recovery boundary |
| Read the complete mouse through an assumed BK3633 UART/SPI pinout | **No** | Physical identity, electrical access, security and independent entry remain unproven |

The root agent has now validated wired logical settings reads including the
120-byte `0x0A` response and the separate known sensor/polling/key/profile
records. The locally archived settings snapshot is useful progress; it remains
**settings only**, not a firmware dump, a complete macro-bank backup or a full
restore image. Do not poll or expand commands in this recovery task.

An acceptable future backup manifest must state chip/board identity, proven
capacity and address units, reader/tool hash, every covered range, raw-versus-
decrypted/CRC format, excluded security regions, and two independently acquired
matching file hashes. A successful file read still requires an independently
proven entry and compatible restore path. None of those checks should be marked
PASS merely because USB PID/version or an application checksum matches.

## Smallest next evidence request, without repeating FCC questions

The chip-name/board-photo part of this request was subsequently supplied as
described above. Further action should target the missing board routing and
reader/entry guarantees, not ask the user to transcribe the same IC again. The
prepared service message below is an earlier alternative, not an action that
must precede the user's chosen board investigation. No message has been sent.

The lowest-impact next step is a **manufacturer service answer**, not a USB
experiment. The current [FURYCUBE contact page](https://www.furycube.com/pages/contact)
lists `service@furycube.com` for other support. Beken also lists technical
support through its [official support/download page](https://www.bekencorp.com/index/services/download.html?cid=2).
No message was sent. This is a prepared request the user can review/send:

> G9 PRO, PAW3311/OLED, wired USB 1D57:2148, stock application 1.08; no FCC ID is
> printed on this unit. Your product page calls its MCU BK3533. Please provide
> the exact controller part/package and board revision identification method;
> a supported full readback tool/version and file format; and the documented
> recovery entry that works when the application is invalid. Does readback or
> entry change flash, flash protection, OTP/eFuse, pairing or calibration? Please
> provide the exact board pad/pin/voltage/reset instructions and confirm whether
> all update-affected regions plus device-specific data can be restored from a
> saved image. Is BK3533 a distinct part from BK3633 for these purposes?

If the vendor cannot identify the board externally, the smallest useful
physical evidence is an **unpowered board photograph**, performed only if the
user elects to open it or uses a repair technician: one overview showing the
PCB revision/silkscreen, plus a sharply focused, evenly lit close-up of the
complete main-controller top marking and nearby labelled test pads. Photograph
the opposite side only if the marking/revision is on it. This is not a request
to connect a programmer, short pads, scrape coatings, remove an IC, or guess a
strap. Neither a generic mouse picture nor another product's PCB substitutes
for this unit. No G9 disassembly/pad-wiring procedure has been verified here.

For the full-dump route, a matched board pinout, compatible reader and
app-independent entry must precede two read-only acquisitions and restoration
qualification. A matching spare board or exact manufacturer procedure can avoid
using the only working mouse for a destructive test. Alternatively, verified
USB app-only rollback can suffice if entry survives the relevant app failure,
the exact stock app is available, and every other affected byte is preserved
or restorable. Neither route is established. **No proven complete recovery and
no firmware installation** remains the current status.
