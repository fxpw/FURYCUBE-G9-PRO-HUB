# Normal-application USB readback and recovery limits

Updated: 2026-11-09. **No supported arbitrary-address firmware read was found in
the audited normal application. A complete backup of the current device has not
been obtained. Firmware installation and loader-entry experiments remain blocked
by the recovery requirement.** See [RECOVERY.md](RECOVERY.md).

This is an offline audit of original files. No new USB command, unfamiliar GET,
loader transition, flash read/write, or settings change was sent to a mouse.
The standalone client's send allowlist and device source were not changed by
that original audit. A later, separately validated configuration implementation
now includes the known GET04/06/07/0A schemas. A complete wired settings read and
restoration of one timer have passed; they do not provide arbitrary-address flash
access or a firmware backup. See [settings parity](SETTINGS-PARITY.md) and
[readback freshness limits](READ-HANDSHAKE.md).
Installing a new flash-reading firmware first would be circular: it cannot
provide the required backup of the original state before installation.

## Scope and identity

The application came from the official G9 PRO 1.08 updater, whose SHA256 is:

```text
558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4
```

The original BIN resource 129 / language 2052 is 134140 bytes, SHA256:

```text
f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc
```

The existing normalized analysis file is 126252 bytes, SHA256:

```text
e0cc9bcb8930ea430d8bc14fefb746f189f0f5f743b81c0b32a5b161f7fba5db
```

Code addresses below use its derived effective base `0x28790`. RAM addresses,
flash-driver address arguments, and updater program addresses are labelled
separately; they must not be silently treated as one raw-dump representation.

This establishes behavior of that downloaded application, not byte-for-byte
identity with the installed image. Matching normal PID `1D57:2148`, version 1.08
or HID capabilities is insufficient to establish the complete installed image,
board revision, bootloader or chip security state. In particular, manufacturer
G9 material names BK3533, while earlier platform research used BK3633 documents;
that hardware identification remains qualified. The in-image control/data-flow
findings here do not require assuming that those chip names are interchangeable.

The receiver `1D57:FA60` is a separate transport and firmware image. Its relay
behavior is not inferred to be an independently audited full-flash reader.

## USB request entry points

Report IDs use an explicit `0x` prefix throughout this document. In particular,
**0x0A is decimal 10 (all-data/DPI settings); 0x10 is decimal 16 (update entry).**
They are different IDs and must never be abbreviated as an ambiguous GET10.

The normal USB setup dispatcher `0x3855A..0x3858C` distinguishes standard and HID
class requests. Vendor/reserved request types take the rejection/stall path
`0x3856A..0x38570`; there is no vendor-type flash-read dispatcher in that path.
Standard descriptor queries use fixed descriptor tables around `0x44A41`.
Status/idle/protocol requests return short fixed device state, not caller-chosen
memory addresses.

HID class requests enter `0x388A8`. For a granted non-0xA0 GET_REPORT,
`0x3892E..0x3898A` consumes the ready flag, selects the fixed prepared-response
RAM at `0x400F91`, copies to staging RAM `0x40124D`, and schedules the USB IN
transfer. `GET 0xA0` returns only an eight-byte ready/not-ready record. A transfer
length is not a flash offset or pointer.

The response pointer stored at request-struct offset 4 is assigned from fixed
RAM-buffer expressions. Valid normal requests do not supply a 32-bit source
address. Unsupported lengths/IDs must not be used as memory-corruption probes
or described as a supported recovery interface. No such probes were performed.

## Read handshake and side effects

The normal read query is carried in SET_REPORT 0xA0 and processed asynchronously
by `0x34216..0x3431C`. Its selected report ID, length byte and one-byte selector
are stored in a small request structure at `0x400F88`. This is a named-record
request, not an address/length flash-read primitive.

1. The host must begin with 0xA0 ready 0 and no unconsumed response.
2. The read state machine clears ready, records the query and prepares a reply.
3. A **nonzero selector** requests a profile or macro record load before reply
   assembly. For most report IDs, selector-1 selects a profile. ID 0x09 instead
   selects a macro slot.
4. A **zero selector** follows `0x342B2..0x342BE`: it skips the requested flash
   load, uses the active RAM snapshot and a two-byte header. It is not equivalent
   to selector 1. With a nonzero selector, the response has a three-byte header
   including that selector.
5. `Get_Report_Data` at `0x33B84` fills the reply, after which `0x3430E` sets
   ready 1. The HID GET consumes the ready flag; the state machine waits for this
   consumption before accepting another transaction.

Every Get_Report_Data case, including default/unsupported IDs, reaches
`0x33C9A..0x33CA6`, which reloads the active profile through `0x30B78` after reply
assembly. Entry also calls the wake/settings routine `0x34AF8`, which can affect
OLED/power/inactivity state. Therefore even selector 0 is **not** a passive,
side-effect-free observation. In particular, GET 0x0A must not be polled as a neutral
live-DPI sensor. Passive generic HID input remains the preferred telemetry source.

The profile loader verifies record fields/checksums and can substitute defaults.
Consequently these replies may represent normalized logical settings rather than
all original bytes stored in flash.

A ready 1 acknowledgment alone does not identify the returned record. Previous
local diagnosis saw a queued macro read return a prior version reply
`0B 05 01 01 08 ...`, and another queued read later returned a stale macro reply.
Exact response ID/shape/selector/checksum and fresh idle/grant ordering are
required. Blindly repeating GETs or issuing a new query while ready 1 can consume
the previous transaction, not the newly requested data.

## Supported read-data matrix

The ROM byte-switch table at CPU address `0x33BA6` has nine entries for IDs 0x04..0x0C:

```text
09 06 7A 1B 29 38 4D 63 72 83 7A
```

Each target is `table_address + 2 * entry`; the final 7A is the default target.
The body is cleared for 132 bytes before dispatch. This table, fixed data sources
and explicit branches give the following matrix.

All numbers in the first column are hexadecimal. A logical frame with a nonzero
selector has three header bytes; a selector 0 frame has two. The descriptor column
is the declared feature length **including report ID** in the audited image,
not a promise that every longer logical transfer is accepted by Windows or by
the receiver. Known historical transport padding/fragmentation is separate.

| ID | Data handler | Prepared body and source | Body bytes / logical bytes with selector | Descriptor bytes | Read effects / limits |
|---|---|---|---:|---:|---|
| 0x04 | `0x33BB2` | Sensor/DPI settings: 47 bytes from RAM `0x40103A`, followed by checksum converted to big-endian | 49 / 52 | 52 | Selected profile load for nonzero selector; active profile reload afterward |
| 0x05 | Default `0x33C9A` | No lighting read implementation; cleared body only | No valid data record | 13 | Declared HID report does not imply a GET handler; do not use as settings readback |
| 0x06 | `0x33BDC` | Two polling fields at RAM `0x401038..0x401039` | 2 / 5 | 9 | Historical host frame can be padded; no address argument |
| 0x07 | `0x33BF8` | Four bytes from RAM `0x40102A..0x40102D`, labelled RF-power by firmware | 4 / 7 | 8 | Fixed global/profile fields; not a raw region reader |
| 0x08 | `0x33C16` | 54 button-table bytes at RAM `0x401076`, plus checksum converted to big-endian | 56 / 59 | 59 | Current client strictly validates the 59-byte logical profile record |
| 0x09 | `0x33C40` | 106 macro bytes at RAM `0x4010AE`, plus checksum converted to big-endian | 108 / 111 | 64 | Fixed macro slot load; transport framing differs from logical body size; no arbitrary offset |
| 0x0A | `0x33C6C` | 117 bytes from RAM `0x401038`, labelled all-data | 117 / 120 | 8 | Logical profile snapshot, not application flash; this long transfer is not newly validated on hardware |
| 0x0B | `0x33C8A` | Two constants 0x01, 0x08 | 2 / 5 | 8 | Known eight-byte host transfer; logical length 5/8 header variants do not make a firmware dump |
| 0x0C | `0x33CAC` | Active profile+1/complement and macro profile+1/complement from RAM `0x40102C` / `0x40102E` | 4 / 7 | 6 | Existing client uses a validated eight-byte transfer; fixed state fields only |
| 0xA0 | `0x3898C` | Ready flag plus zeros | Eight-byte ACK frame | 8 | Does not return flash data |
| 0x10 | Pre-dispatch `0x34250..0x34292` | No supported read operation; an 0xA0 query naming this ID can enter update mode before read/write dispatch | Not a readback | 8 | Flash marker write/reset path; never an exploratory read |
| Other IDs, including 0x22..0x2E | Default `0x33C9A` in read-data dispatch | No data-specific read implementation | Cleared body only | Several IDs are declared | Can still wake/reload active profile; not discovery or read-flash commands |

The normal HID descriptor at `0x44890..0x4497B` declares additional feature IDs
0x22..0x2E, but neither a descriptor declaration nor a large feature capability
creates a normal-application flash-read handler. ID 0x23 is declared twice; combined
feature bits explain the 262-byte maximum capability and must not be interpreted
as 262 bytes of firmware-readable flash.

## Where the settings actually come from

The application has a general internal flash-read helper at `0x39BC2`, but its
normal-USB-reachable record loaders supply bounded, fixed address expressions.
The existence of that internal helper does not expose its address parameter over
USB.

| Record loader | Caller-controlled value | Bound | Flash-read address argument | Fixed size |
|---|---|---|---|---:|
| Profile loader `0x30B78` | One-byte zero-based profile index derived from selector-1 | Values greater than 4 become 0 | Five-entry table at `0x4469C`: `0x7C080`, `0x7C100`, `0x7C180`, `0x7C200`, `0x7C280` | 117 bytes |
| Macro loader `0x30FFA` | One-byte zero-based macro index derived from selector-1 | Values greater than or equal to 32 become 0 | `0x7D000 + index * 0x80`, ending at `0x7DF80` | 108 bytes |

The helper receives `(r0=0, r1=address, r2=length, r3=destination, stack_arg=0)`
at these sites. Profile/macro selectors do not escape their tables by wrapping:
out-of-range indices are clamped back to the first record.

Reading every named record could preserve some user configuration after further
transport validation. It would still omit application code, bootloader/stack,
unused/erased bytes, portions of global/pairing/calibration data and any protected
areas. The macro path exposes 108 bytes of each 128-byte slot, not all slot bytes.
GET 0x0A copies a normalized RAM representation; it includes some checksum words in
RAM endianness and can reflect fallback/default values. None of these is a raw,
complete flash backup or evidence of complete restoration.

## Write dispatch and why loader entry is not a harmless probe

The normal SET data switch at `0x33D20` covers IDs 0x04..0x10. IDs 0x04/0x05/0x06/0x07/0x08/0x09
write named settings; 0x0A schedules a default/reset operation, 0x0B schedules state 2,
0x0C changes active profile fields, and 0x10 calls `0x39446` to enter update mode.
IDs 0x0D/0x0E/0x0F and out-of-range IDs take the return default. No SET case establishes
a caller-selected flash-read source address for a later GET. This is an audit
inventory, not a list of commands to try.

There is an additional pre-dispatch special handling of 0x10 in the read/write
state machine at `0x34250..0x34292`, which can also reach the update-entry
function before read/write operation selection. An **0xA0 query naming 0x10** is
unsafe as an exploratory read. This finding does not concern GET 0x0A, which is
the separate all-data/DPI settings operation.

Update entry `0x39446` passes the 16-byte marker at CPU address `0x447EC`,
destination flash-driver address `0x7D000`, to helper `0x39CB0`, then resets.
That is the same flash-driver address space used by the macro loader above:
there is no separate USB address unit in the two call sites.

Helper `0x39CB0` reads a 1024-byte aligned window through `0x39BC2`, calls the
erase helper `0x39C56` with length 4096, copies the marker into the saved window,
then writes 1024 bytes through `0x39BF0`. Erase granularity and its consequences
for the actual board still require hardware-specific confirmation; the shared
address and unequal requested erase/restore sizes are visible in the code.
This path cannot be treated as read-only or as preserving all macro/config bytes.
Do not run it merely to discover whether a loader enumerates.

## OEM updater cross-check

A selective recheck of the original PE confirmed normal-device selection at
`0x402FA0..0x40305A` and its feature 0x10 send at `0x403073..0x403095`. Its HID imports
include enumerate/open, feature-send, write and interrupt-read; it has no imported
`hid_get_feature_report` normal-mode backup API. The analyzed update workflow
`0x402F50..0x403CBB` performs transition, loader commands, erase/program and reply
checks. No pre-update flash-dump/save operation was identified in that workflow.

The known updater erase range `[0x2B000,0x51000)` is wider than its program range
`[0x2B00A,0x4C00A)`. Its range-CRC command/reply does not return original bytes;
the checked reply header at `0x403A3F..0x403A68` is not a complete readback or
backup. An undocumented loader opcode may exist, but it is not established by
this application audit, and entering that loader is already a state-changing
operation. No candidate hidden read-flash command was validated or proposed for
live testing here.

These are bounded negative findings for the inspected application/update paths,
not a claim that the chip has no manufacturer debug/download interface.

## Offline checks and next evidence

A one-off Unicorn harness executed the original Get_Report_Data instructions for
all 256 report-ID values. It observed eight fixed-data cases and 248 zero/default
cases; all 256 reached the active-profile restore call. ROM byte-switch/memcpy,
wake/logging and active-profile reload were explicitly stubbed. The same harness
executed original profile/macro address-selection instructions for all 256 input
indices each, stopping at the internal flash-read call before executing it:

```text
PASS all 256 GET choices: 8 fixed-data cases, 248 zero/default cases
PASS all 256 profile indices: 5 fixed flash-read addresses, fixed length 117
PASS all 256 macro indices: 32 fixed flash-read addresses, fixed length 108
OFFLINE ONLY; no USB or physical flash access
```

This direct-function test does not exercise the outer state-machine pre-dispatch
trap for 0x10 and does not make an 0xA0 query for that ID safe. The separate static
entry-point analysis above supplies that essential distinction.

The local scratch fixture is `bin/audit_usb_readback_probe.py`; original images
and scratch artifacts remain outside public source. The switch bytes/formulas,
source hashes and handler addresses above permit independent static reproduction
with the existing `tools/firmware_probe.py`. Emulation confirms those selected
instructions under stated stubs; it does not establish installed-image identity,
physical flash layout, complete restoration or a working hardware loader.

The next useful recovery evidence is an exact board/chip identification plus a
manufacturer-supported, app-independent entry/read/restore interface. Its first
operation must be a proven read that leaves original flash unchanged. A verified
dump must cover all regions an update or its entry procedure can modify, with
format/addressing/security and restore semantics understood. Until then, the
correct status remains **no full backup, no proven full restore, no flashing**.
