# Exact G9 OEM updater: USB command inventory and backup limits

2026-11-09. This is a host-binary audit, not a discovered loader specification or
instructions to send packets. No native vendor program, USB/UART connection,
loader transition, reset, erase or programming operation was performed.

Audited input: official `G9_PRO_BT_Ver1.08_Upgrade_20251028.rar`, `Update.exe`,
3,652,608 bytes, SHA256
`558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4`.
The [official download page](https://www.furycube.com/pages/g9-pro) is the source;
vendor files remain outside the public repository. Findings apply to that exact
binary, not all Beken tools or all `A745:0033` firmware revisions.

**The host can erase and program its supplied application. It has no identified
flash-dump operation. Its reads in the update workflow collect acknowledgements,
not a pre-update copy. That does not establish that the device-side loader lacks
an undocumented read operation.**

## Complete imported HID-operation coverage

All `.text` references to the eight imported `hidapi.dll` functions were counted.
There are six `hid_write` call sites, five `hid_read` call sites, and two
`hid_send_feature_report` sites. All transfers are inside the one workflow at
`0x402F50..0x403CD6`; there is no additional imported HID transfer path elsewhere.
The separate discovery helpers at `0x405330` and `0x405470` only enumerate/open.
No `hid_get_feature_report` is imported.

The pure static parser [audit_g9_updater_usb.py](../tools/audit_g9_updater_usb.py)
checks the hash, every HID import and every reference against the pinned image:

```sh
tools/pyenv/bin/python tools/audit_g9_updater_usb.py
```

| Function / call instruction VAs | Host use |
|---|---|
| `hid_write`: `4032C7`, `40349C`, `4035DD` | Initial sector erase, main 64-KiB erase, main 4-KiB erase |
| `hid_write`: `40381A` | Repeated fragments of a programming command |
| `hid_write`: `4039D9` | One final range-check request |
| `hid_write`: `403B03` | Three reset requests |
| `hid_read`: `4032F8`, `4034B9`, `4035FA` | One reply after each erase |
| `hid_read`: `403853` | One reply after all fragments of each 4-KiB write |
| `hid_read`: `4039F6` | One final range-check reply |
| Feature send: `403095`, `403C06` | Application update-entry command and post-return application command |

`hid_read` here is interrupt-HID input. It is not a HID feature GET, and a
receive call alone does not mean it reads firmware bytes. Generic Win32 file
APIs and the large MFC/UI runtime are also linked; the bounded conclusion above
comes from all HID transfer references and their data flow, not string absence.
Firmware strings such as `flash_read` inside the embedded BIN resource are
mouse application strings, not hidden x86 host-dump functions.

## Discovery and version: no loader identity query

The host first selects normal `1D57:2142`, otherwise `1D57:2148`, requiring usage
page `0x0B`, usage 0. It then enumerates loader `A745:0033` and opens the first
path that succeeds. The loader selection checks no serial number, chip ID,
capacity, security state, installed boot/stack hash or command capabilities.
After opening that handle, the next update operation is erase.

The normal discovery helper obtains the displayed device version from the HID
enumeration structure's `release_number` at offset `0x0C` (`0x405382/0x405421`).
The imported `hid_version()` reports the host HIDAPI library's version. Neither
is a loader flash-read command. The two feature transmissions are exactly:

| Host call | Supplied bytes and length | Classification |
|---|---|---|
| `0x403095` | `10 08 00 00 00 00 00 00`, 8 bytes | Normal-app update-entry SET; requires the working app, not independent recovery |
| `0x403C06` | `0B 08 00 00 00 00 00 00`, 8 bytes | Write-only application command after reset/re-enumeration; no version GET or flash data is received here |

The previously documented NVM effects of normal app entry are in
[RECOVERY.md](RECOVERY.md); they are not repeated as an entry procedure here.
The host contains no verified alternate electrical/button/emergency entry
sequence for an unusable main application.

## Loader packet shapes actually emitted

Each loader `hid_write` supplies 65 bytes: report ID 0 followed by 64 logical bytes.
The following table omits that report ID. `LE32(x)` denotes four little-endian
bytes. Only defined command fields are shown; unused report padding is not a
protocol guarantee. Programming fragments are padded with `FF` after the final
logical byte. Incoming `hid_read` requests have a 65-byte buffer; the original
host's acknowledgement checks use reply byte 0 as the first `04` below.

| Operation | Logical request | Reply fields checked by this host |
|---|---|---|
|4-KiB erase | `01 E0 FC FF F4 06 00 0F 20 LE32(address)` | Prefix `04 0E FF 01 E0 FC F4 07 00 0F`; byte 11=`20`; bytes 12..15=address |
|64-KiB erase | `01 E0 FC FF F4 06 00 0F D8 LE32(address)` | Same prefix; byte 11=`D8`; bytes 12..15=address |
| Write 4 KiB | `01 E0 FC FF F4 05 10 07 LE32(address) data[4096]` | Prefix `04 0E FF 01 E0 FC F4 06 00 07`; bytes 11..14=address |
| Final range check | `01 E0 FC 09 10 LE32(start) LE32(endInclusive)` | **Only** `04 0E 08 01 E0 FC 10`, seven bytes |
| Reset / return | `01 E0 FC 02 0E A5` | Sent three times; no reply read at this stage |

The write stream has 4108 logical bytes, split into 65 HID reports: 64 full 64-byte
fragments and a final 12-byte fragment plus `FF` padding. A single reply follows
the whole 4-KiB command, not every fragment. Erase byte 10 and write byte 10 are
not interpreted by the host. Their meaning, including whether they are status,
cannot be established from this check alone.

The final range command is commonly described in prior project notes as
“range CRC”. The expected event length leaves four bytes after its seven-byte
header, consistent with a 32-bit result. **This updater neither computes a host
reference CRC nor reads, compares, copies or saves those four result bytes.**
The device-side calculation/status meaning remains unverified here. It is not
byte-for-byte readback, and no complete flash range is returned to a host file.

The `hid_write` return values are not checked in this workflow. Reply reads
are tested for zero, not the exact expected length; the later code checks the
listed fields. A successful-looking host state therefore is not independent
proof of complete, correct flash programming.

## Exact original erase/program scope

Constructor `0x403F16` sets the start to `0x2B00A`. With the original embedded
resource and rounded transfer size `0x21000`, original workflow instructions
produce:

| Phase | Addresses / size |
|---|---|
| Prefix sector erases | `20` at `2B000`, `2C000`, `2D000`, `2E000`, `2F000` |
| Main erases | `D8` at `30000` and `40000`, then `20` at `50000` |
| Total erase interval | `[0x2B000,0x51000)` |
| Program interval |33 commands: `[0x2B00A,0x4C00A)` |
| Final range arguments | Start `0x2B00A`, inclusive end `0x4C009` |

The erase calculation and write sizes come from `0x4031F4..0x4036CF` and
`0x403700..0x40391A`. The resulting normal modeled run contains 2157 loader
`hid_write` reports and 42 `hid_read` replies: eight erase replies, 33 write
replies and one range-check reply. These are offline instruction results, not
a physical capture or proof of flash geometry/security behavior.

The main erase/program sequence leaves physical `[0,0x2B000)` outside its
requested ranges. That is useful for a narrower **application rollback using
a preserved valid BIM/stack**, rather than demanding survival of BIM erasure
or an immutable-ROM-only route. However, erase extends beyond the write range,
and no old contents in those additional bytes are saved. All entry and loader
side effects must also be included before promising preservation of other data.

An exact original app, independently reachable USB entry with the main app
unusable, and proven preservation/restoration of all other affected data could
support that narrower recovery contract. The stock host's working-app entry
does not establish it. These packet bounds alone do not prove the installed
BIM layout, a compatible emergency-entry path or a complete rollback.

## Bounded negative finding and offline checks

No host function was identified that issues a read-address/read-length flash
command and then copies received bytes to a backup buffer/file. All five HID
receive sites are accounted for above. That is evidence about this exact x86
host; device-side loader handlers are absent from the app-only resource, so
unsupported read opcodes cannot be enumerated or ruled out by this audit.
Another SDK reader is not interchangeable merely because both use USB/HID.

Original workflow instructions were also run in Unicorn with **all** HID,
OS/clock and CRT calls replaced by local test doubles, synthetic input data
and replies. Eight cases check the normal trace, a changed four-byte range
result, corrupted range header, short returned read count, two altered
uninterpreted byte 10 values and wrong erase/write reply addresses. Altered
range-result bytes and byte 10 values still reach host completion; corrupted
checked fields take the corresponding failure state. The short-count fixture
fills the synthetic buffer deliberately: it proves a missing count check,
not that a real one-byte reply contains all required fields.

The normal trace reassembles all 33 outbound blocks exactly to the synthetic
source data. These are checks of host packet construction/control flow only;
no loader firmware, USB device, erase/write hardware or recovery was exercised.
Local ignored evidence is under `evidence/g9-updater-usb/`, including static
disassembly/import references and the offline replay/results. No new firmware
candidate, installer or flash-dump claim results from this work.
