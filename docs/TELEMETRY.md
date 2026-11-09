# G9 PRO telemetry: proven input and remaining DPI work

The decoder/cache is pure. `src/G9TelemetryReader.cs` adds a passive native
reader, integrated into the HUB. On 2026-11-09 it connected to the wired
`1D57:2148` input collection and received a fresh **100% battery** value.
No feature request, input hook, driver installation, firmware command or mouse
setting change is needed for this input path. A fresh DPI-level event and live
surprise-removal/cancellation still need physical validation.

## Safe source: passive HID input

Use the normal G9 collection with VID `1D57`, PID `2148` (wired) or `FA60`
(receiver), usage page `000A`, usage `0000`, input report length **5**. Existing
capability evidence identifies it as `MI_02&Col03`. This is a different collection
from the page `000B` configuration feature interface. Do not use the keyboard,
mouse, consumer-control or bootloader collection as a substitute.

| Input bytes, including report ID | Meaning | Evidence |
|---|---|---|
| `03 C9 40 01 56` | Battery86%, discharging | Captured receiver: `evidence/protocol-version.txt` and `protocol-readback-variants.txt` |
| `03 C9 40 03 64` | Battery100%, charging | Captured wired: `evidence/protocol-wired-read-replugged.txt` and `protocol-wired-before.txt` |
| `03 C9 40 state percent` | `state`1=discharging,2=full,3=charging; percent0..100 | Firmware `0x319B6` / `0x319E8`; native HUB `0x41D47F..0x41D5F9` |
| `03 C9 41 state percent` | Same fields, recognized by old web HUB | `evidence/web-driver.js`, connection parser around byte1393386; this variant was not captured on this device |
| `03 C9 10 stage 00` | Current **one-based DPI stage**,1..8 | Firmware `0x31A0C..0x31A58`, native HUB `0x41CF74..0x41CFBF` and `0x41D3F4..0x41D43F`; physical input capture still pending |

Firmware addresses refer to `evidence/g9pro-firmware-normalized.bin`, effective
base `0x28790`. The battery formatter writes model byte `C9`, command `40`,
state, and `100 - RAM[0x400AC6]`. The DPI formatter reads actual runtime stage
from `0x40104F`, writes command `10`, and zeroes its last byte. The physical DPI
handler updates that stage at `0x3256A..0x32574` and subsequently calls the
formatter.

Do not copy the old web HUB's missing-battery fallback to100%. The decoder
preserves the explicit percent even if the state says full; it never replaces
missing or expired values with100 or0. Each HID session owns a separate cache;
disconnect, device selection and profile changes must invalidate it. The caller
chooses an explicit maximum age. Invalid/out-of-order packets do not renew it.

## DPI stage is not the numeric DPI

The passive event has no numeric DPI value. The stage must be joined to a proven
stage-to-DPI table for the same device/profile before a numeric DPI widget can be
enabled. `TelemetrySnapshot.Dpi` therefore remains null; `DpiStage` is available
as a separate, accurately labelled value.

Reading stock `GET10` is **not** a neutral runtime-DPI observer. The read FSM at
`0x342C2..0x34310` requests a flash profile load before serving the report, then
`0x33C9A..0x33CA6` reloads the active profile. Consequently, its stage field is a
saved/profile snapshot and the operation can change RAM configuration. Do not
poll it to keep a live widget fresh.

For future offline parsing, its proven query is `A0 0A 78 00 01 00 00 00`,
logical reply120 bytes, profile1 selector. It must use the same strict
idle/grant/single-GET discipline as other known feature reads. The reply's
three-byte header precedes117 RAM bytes beginning at `0x401038`; DPI stage is
reply byte26 (RAM `0x40104F`). This is documentation, not permission or a new
send allowlist entry. No GET10 code was added to the standalone client.

The shared web3311 decoder is unsuitable as an unquestioned reference: its
lookup table contains an out-of-byte value404 and duplicated entries, and its
generic fallback can invent50/100 DPI for unknown codes. Native parsing of
configuration data also depends on its native transport's converted format.
Resolve the G9 sensor encoding from firmware and a captured, known configuration
before implementing numeric table conversion. Never assume factory DPI values.

## Passive HUB reader and possible CLI

`G9TelemetryReader` is a standalone internal class with no `Main` and no vendor
DLL dependency. Compile `src/G9Telemetry.cs` and `src/G9TelemetryReader.cs` into
the HUB. `Start()` queues lazy initialization; `Snapshot` / `CurrentSnapshot`
return a thread-safe snapshot, expiring battery at90 seconds and stage at5
minutes. Before the first valid packet, after a failed read/disconnect, and after
`Dispose`, affected values are null. `LastError` contains any read/discovery
failure; `DevicePath` and `ProductId` identify the selected input collection.

Selection filters `MI_02&Col03` before opening a handle, prefers one wired2148,
falls back to one receiverFA60, and fails closed if the preferred set is
ambiguous. The chosen handle is checked with `HidD_GetAttributes`, preparsed
capabilities and exact input length5. It is opened for shared, overlapped read
only. No keyboard collection is opened.

The reader uses `ReadFile` with a fixed five-byte buffer and one registered
completion wait; it creates no waiting dedicated thread. `Dispose` clears the
cache and calls `CancelIoEx` without waiting in the GUI. Unmanaged buffer,
`OVERLAPPED`, event and device handle remain owned by the pending session until
its completion callback. They are never freed while an IRP may still refer to
them. Completion reads use `GetOverlappedResult(wait:false)` only. A defensive
incomplete notification is retried by a timer, without resetting a potentially
new completion signal. Packet publication precedes the next read to preserve
stage event order. A failed USB stack which never completes cancellation cannot
block a GUI thread; its outstanding IRP memory remains retained safely until
kernel completion/process exit instead of being freed early.

No reconnect/profile query is sent. A disconnected session ends; the owner can
dispose and create a new reader when its device selection changes.

`G9Device.exe telemetry --duration-ms 500 --pid 2148` can safely be implemented
by opening **only** the matched page000A collection for overlapped `ReadFile`,
using shared access, bounded cancellation and the existing selected-device
ambiguity rules. Bind it to the same physical-device/session identity as the
configuration handle. `HidD_GetInputReport` is an active control request and is
unnecessary here. No SET, feature GET, A0 grant, wake request or profile selection
is needed for passive input. A quiet/sleeping mouse returns null fields.

Prefer a persistent reader owned by the HUB session for useful caching: one
short child process may miss infrequent reports. If a child CLI is used, emit
one JSON record containing nullable battery/stage plus their individual receive
times; absence during the window is not a sensor value. Keep the GUI's own
timeout so a failed HID stack cannot block it.

API: namespace `Furycube.G9.Telemetry`; `TelemetryProtocol.TryDecodeInput`,
`IsSupportedInputCollection`; `TelemetryCache(connectionKey).AcceptInput(...)`,
`Snapshot(now,maximumAge)`, `Reset()`. No USB transport is linked by this module.

Validation: `tools/build-telemetry-tests.cmd` exercises captured battery bytes,
normal/bootloader/interface identity, stage limits, malformed packets, independent
freshness, clock rollback, disconnect reset and absence handling without HID.
Reader fixtures cover selection/ambiguity, collection exclusions, duplicate paths
and lifecycle before `Start`; no native HID calls are made by either test suite.
The HUB's live check confirms initial connection and battery reception; it does
not validate native cancellation on physical disconnect. Restart the HUB after
disconnecting the selected mouse, because automatic reconnect is not implemented.
