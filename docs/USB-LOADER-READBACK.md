# USB loader readback: G9 OEM updater versus SDK HID tool

Updated: 2026-11-09. This comparison uses local pinned PE files as data and
isolated original-function emulation with sink I/O. No vendor process, USB
handle, loader transition, reset or programming operation was performed.

**Result:** the unmodified SDK tool rejects the USB identity expected by the
G9 updater, and its dump workflow uses different command framing plus flash
write preparation. It is not an established G9 reader. This is not proof that
the G9 USB loader cannot read: that loader's actual read handlers and a safe
main-application-independent entry remain unverified.

## Pinned inputs

| Input | SHA256 |
|---|---|
| G9 OEM `Update.exe`, official 1.08 package | `558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4` |
| SDK `HID Flash Download Tool_V2.5.5.exe` | `9240adf66b69d0e61bc6f87089bac441d042bc97c9a159401c174f790b447e0c` |

The OEM file is under `evidence/firmware-readonly/`; the SDK executable is under
`evidence/recovery-uart/usb/烧录工具/`, extracted from the pinned
[Beken SDK download-tools archive](https://github.com/bekencorp/bk3633_mesh_sdk/tree/70d28da4d0c97d45a119644d9eb9abd0db459b14/download_tools).
All addresses below are x86 PE virtual addresses, not MCU flash addresses.

## Actual discovery filters

| Evidence | G9 OEM updater | SDK HID tool 2.5.5 |
|---|---|---|
| Discovery | `hid_enumerate`, import `0x57D930` | SetupAPI HID enumeration `0x40FA50..0x40FB55` |
| Application identity | `1D57:2142` / `1D57:2148`, `0x402FA0..0x40305A` | Neither accepted by the validator |
| Loader/adapter identity | `0x4030BB..0x4030C2` pushes PID `0x0033`, VID `0xA745` into `hid_enumerate`; repeated helper `0x405470` agrees | `0x40FB8D` calls real `HidD_GetAttributes` through `0x41E5B2`; `0x40FB92..0x40FBA2` compares `HIDD_ATTRIBUTES.VendorID == 0x10C4`, `ProductID == 0x0033` |
| Nonmatching device | Not returned by that loader enumeration | Handle closed at `0x40FBF7..0x40FBFD`, return false |
| Additional inspected selection | OEM application path checks usage fields before opening | SDK validator has no HID usage-page/usage or firmware-version check; accepted devices may have host input-buffer count enlarged |

The shared product ID `0033` is insufficient. Original SDK validator instructions
were emulated with synthetic attributes: `10C4:0033` accepted; `A745:0033`,
`10C4:0034` and `1D57:2148` rejected and closed. No payload was sent in these four
cases. A VID-constant patch would remove a guard, not establish protocol
compatibility, and is not proposed.

## Transport and logical framing

Both hosts can submit 65-byte Windows HID output buffers containing a leading
zero report-ID byte and up to 64 logical bytes. That envelope alone does not
make the protocols interchangeable.

| Layer | G9 loader expected by OEM | SDK adapter protocol |
|---|---|---|
| Write API | `hid_write`, import `0x57D920` | `WriteFile`, import `0x603394`, wrapper `0x40FE20` |
| Read API | `hid_read`, import `0x57D928` | `ReadFile`, import `0x603388`, wrapper `0x40FD20` |
| Logical messages | HCI-style `01 E0 FC ...`; replies start `04 0E ...` | Bridge messages beginning `00`, `03`, `04`, `05`, `07`, `08`, `0D`, without that HCI prefix |
| Write wrapper | OEM stages its HCI stream after report ID zero | SDK `0x40FEB3..0x40FEE1` copies the logical payload after report ID zero, writes 65 bytes |
| Read wrapper | OEM checks command-specific event prefixes | SDK reads 65 bytes, discards the first byte and copies the requested logical count (`0x40FD6D..0x40FDC4`) |

Representative OEM logical messages are reconstructed from the actual encoder:

- Erase via SPI opcode: `01 E0 FC FF F4 06 00 0F <20-or-D8> <addressLE32>`;
  reply prefix `04 0E FF 01 E0 FC F4 07 00 0F`, followed by opcode/address.
  Relevant encoder/checks: `0x403230..0x4033A9`, `0x4033E0..0x4036B7`.
- Program block: `01 E0 FC FF F4 05 10 07 <addressLE32> <4096 data bytes>`,
  segmented through HID writes; reply prefix `04 0E FF 01 E0 FC F4 06 00 07`.
  Relevant code: `0x403708..0x4038F6`.
- Verification request: `01 E0 FC 09 10 <startLE32> <endInclusiveLE32>`.
  The host checks only `04 0E 08 01 E0 FC 10`; it does not compare the following
  four bytes. Do not label them a proven flash dump or verified CRC result.
  Relevant code: `0x403920..0x403A68`.

The audited OEM flow contains erase/program/verification exchanges, not a
full-flash data-read loop. Its HCI-shaped envelope resembles the separately
audited BKFIL UART protocol; that resemblance does not prove the installed
G9 USB loader implements the UART read-page subcommand. No such command was
sent to the mouse.

## SDK Dump FLASH: preparation, request and data

The archive's saved chip selection is index 17. It is a tool configuration, not
the identity of the G9. `0x4058C0` calls the selected preparation pointer through
`0x41E060`, table `0x608FB0`; index 17 points to `0x417000`.

The inspected index-17 path performs the following **before** requesting dump
data:

1. Conditional mode/configuration helper `0x416F10`: logical
   `03 00 00 64 <state-byte>` and a one-byte response check against `EE`.
2. `0D` padded to 64 logical bytes, then `04 D2` padded to 64, **151 times**.
   Further conditional initialization/reopening follows; its board effects are
   not qualified as read-only.
3. `0x41C370` queries flash MID through `0x41C180`: logical
   `07 9F 00 00 00 00 03`, padded, then reads three MID bytes.
4. For the ordinary nonzero-MID branch, `0x41C230` emits `04 06` (write enable),
   then `0x41C2C0` emits a register write such as
   `08 01 00 00 00 00 01 00`; status is polled with `05 00 05` and one-byte
   reads. Other recognized MID branches select different register values or
   opcode `31`. This is not a read-only preparation contract.

Original-function emulation of the existing-access branch, with synthetic MID
`0x13311C`, recorded those frames: 157 writes in total, including write enable
and status-register write. MID zero instead returned failure after the MID
query. These fixtures establish original host behavior, not the mouse's MID,
security state or accepted commands; the conditional external mode/reopen
branch was not executed in the emulator.

After preparation, `0x405938` calls `0x405670` with operation **`0x00`**. Its
64-byte logical request is:

| Logical offsets | Encoding / source |
|---|---|
| 0 | `00`, dump operation |
| 1..4 | Requested **byte length**, uint32 little-endian |
| 5..6 | Block size, uint16 big-endian, from context `+0x1604` |
| 7..8 | Starting block, uint16 big-endian, from helper `0x402FD0` |
| 9..63 | Zero |

**Correction to the earlier provisional address interpretation:** the first
DWORD is length, not a byte address. Caller `0x4068C7` invokes `0x4052E0`, which
stores the decimal length-control value into the DWORD at `0x40536A..0x40536D`.
The same DWORD reaches the request and later controls the total bytes received
by `0x4057D0`. The separate hex offset control is read in `0x402FD0`, rounded
up to a block boundary, and encoded as starting block. The bound check is
`startBlock * blockSize + length <= context[+0x160C]`. UI callbacks and their
values were stubbed in the request-encoder fixture, not executed.

For synthetic length `0x12340`, block size `0x1000`, starting block 2, the
original encoder emitted `00 40 23 01 00 10 00 00 02` followed by 55 zeros.
These numbers are a fixture, not a requested G9 read range.

`0x4057D0..0x40588B` then receives chunks of at most 64 logical bytes and writes
them to the output stream until the requested total is reached. No per-chunk
address, sequence or data checksum is checked there. It treats only `0xFF` as
transport failure, rather than validating the returned byte count against the
requested count; therefore its success flag alone is insufficient backup
validation. This raw bridge stream is not a strict G9 response schema. No G9
read transport or speculative response parser was added.

## Reproducible offline checks

[`tools/audit_usb_loader_protocol.py`](../tools/audit_usb_loader_protocol.py)
pins both source hashes, checks native opcode/table bytes, and emulates seven
cases: four identity decisions, two MID-preparation branches and one dump
request. USB/API calls are sinks/test sources; an unexpected control transfer
fails. It does not load the PE through Windows or expose a device switch.

Run with the existing research environment, without installing drivers:

```text
wsl -e /mnt/d/repos/fxpw/test/FURYCUBE-G9-PRO/tools/pyenv/bin/python tools/audit_usb_loader_protocol.py
```

All seven cases passed. `physicalDeviceTested` is explicitly false.

## USB recovery conclusions and next evidence

| Question | Current evidence |
|---|---|
| Can the SDK Dump button read the OEM G9 loader unchanged? | No: deterministic `10C4` versus `A745` selection mismatch; distinct logical framing; non-read-only preparation |
| Does this prove USB full read is impossible? | No. The actual G9 loader's read-command implementation has not been obtained or established |
| Can a loader VID/PID or UID establish safe recovery? | No. Enumeration is identity evidence only; entry behavior and restoration are separate |
| Is current application-triggered entry harmless? | No. The audited app entry writes a marker through the macro-bank sector before reset; preserve/restore of those affected bytes remains necessary |
| What can proceed offline? | Locate a matching G9 loader/BIM image or primary command specification; trace its dispatcher/read bounds and entry path. Do not adapt the SDK by guessing headers or replacing VID constants |

An acceptable rollback can be either a complete verified backup/restore, or a
proved USB loader entry that still works when the **main application** is
invalid, together with the known stock 1.08 app and preservation/restoration of
every affected outside-app NVM range. A separate immutable-ROM bypass of a
damaged BIM is not required for an operation that never writes BIM; independent
entry after app damage is still required. The currently available host updater
does not prove this entry, and its normal app transition touches macro storage.
See [entry/erase evidence](RECOVERY.md) and [normal-app read limits](USB-READBACK.md).

No loader transition or unknown USB query is authorized as a discovery probe.
