# DG3: RF state on the mouse display

Updated 2026-11-17. **Installed; USB input recovered and passed 9/9 without a
PC reboot.** The native display visibly showed the expected 9000 baseline.
DG3 is a temporary observation tool, not a radio OLED fix or a custom image
uploader. A fresh radio input check also passed 9/9; the version query timed out
and the owner reported 9000 after moving and stopping the mouse. This remains
ambiguous: an idle value does not establish whether the request was received.

The updater completed 33/33 blocks and closed normally. A complete USB settings
read passed; restoring only the reset screen-button assignment and independently
rereading the profile matched all 117 saved bytes. The input checklist then
collected only the screen key (1/9), consistent with the owner's report that
movement and mouse clicks did not work. The owner confirmed the same failure
after a mouse-interface restart returned success. A subsequent composite-device
restart returned 3010, requesting a Windows reboot; no PC reboot was issued.
PnP status OK did not establish recovery. One independently verified, scoped
USB hub-port cycle returned success and Windows recorded a new G9 arrival,
but the owner still reported no recovery. No PC reboot was issued during those
attempts.

The installed Interception mouse filter was present in G9's reported driver
stack. Its pinned binary rejects CONNECT indices of 10 or higher, while this
boot's registry lists `PointerClass0` through `PointerClass15`. The exact current
G9 class index has not been established; a driver registry key ending in `0015`
is not that proof. The owner then explicitly approved a temporary class-
registration bypass while cycling only the G9 port. It removed Interception
from the G9 stack; the original registration was restored and independently
verified. Input subsequently passed 9/9 with the same firmware and settings,
and the owner confirmed recovery. See [the evidence and limits](WINDOWS-INPUT-RECOVERY.md).
No filter mask or event was changed; the installed driver's unsupported
GET_FILTER IOCTL was deliberately not used.

## Purpose

An ordinary radio version query timed out on the minimal DG2 firmware,
including with the manufacturer's original HIDAPI open/send/read path. The
receiver did not grant a response, while the mouse input checklist passed 9/9.
See [the control images](DG1-DG2-CONTROLS.md) and
[the host transport comparison](OEM-RECEIVER-FRAMING.md).

DG3 keeps the DG2 button, USB endpoint and normal-read fixes. It replaces the
number supplied to the stock DPI text renderer with **9000 + RF state**.
The actual profile, DPI and sensor settings are unchanged. The native display
still prints its original `dpi` suffix: for this temporary build, **9007 means
a diagnostic state, not 9007 DPI**. The value also appears on native USB draws.

## Reading the number

| Displayed number | State at the last native redraw |
| --- | --- |
| 9000 | Normal RF service / idle. Also reachable after completion, abort or a reset of the configuration state. |
| 9001 | RF FIFO/prefix processing, before a typed GET is accepted. |
| 9007 | A GET prefix was parsed and its ACK prepared. |
| 9008 | ACK submitted; waiting for radio TX completion. |
| 9011 | Selected-record preparation after the ACK. |
| 9012 | Reply preparation: waiting for native pending work, then the getter and active-profile reload. |
| 9013 | Reply-fragment service; ready to send a fragment, or finish if none remain. |
| 9014 | Fragment submitted; waiting for TX completion or retry handling. |

This is a **sample, not transaction history**. Fast states may never be visible.
9000 alone cannot distinguish an undelivered request from a completed/aborted
one. A stale redraw and shared native service gates are additional limits.
It does not expose the receiver's USB-ready flag.

After a GET0B prefix has been accepted and the original wake call returns,
DG3 marks only the stock DPI row dirty. It does this only for the existing
ACK prefix `47 / 0B / 08`, with the native AF1/AF2 display guards clear.
There is no direct draw from the RF parser or interrupt handler.

The owner can move the mouse briefly, stop, and let the normal UI service draw
the pending row. Native movement sets its own countdown and activity flag;
DG3 does not write either. The observed twenty serviced countdown steps are
an instruction-model result, not a guaranteed number of milliseconds.
The firmware does not wake or enable an intentionally disabled display for
this observation; existing native display policy remains in effect.

## Exact change and checks

The only new original instruction patches relative to DG2 are the DPI scalar
at `0x3D33A` and RF-parser wake call at `0x305D2`. The original wake executes
first. Tests follow the actual prefix branches, rather than infer execution
order from addresses: the GET state is assigned before that wake call.
The wrapper preserves native return registers and caller-live flag behavior.

No custom frame protocol, version marker, periodic loop hook, new persistent
RAM or NVM write is added. The shared UI countdown is not rearmed. Ordinary
RF response bytes and the normal settings getter remain unchanged. As with
DG2, the stock version string cannot uniquely identify the installed image.

The complete sealed image passed:

- The DG2 suite: 720 normal-read IRQ comparisons, 48 endpoint cases, six
  keyboard-output cases, 512 readiness cases, 30 stock RF comparisons and
  the ordinary screen-key checks.
- Eight real-prefix flows across awake/sleeping cases and four context kinds;
  ten negative or mode cases.
- Native wake/redraw clearing the dirty row, followed by the wrapper requeue;
  actual native movement, countdown, UI dispatcher and font/pixel output.
- Paired wake continuations checking registers and live flags, plus comparison
  with an unpatched renderer producing the same numeric text.

The formatter is an explicit boundary: the fixture applies the actual format
string to the ARM scalar. Font/draw code executes, while I2C and other hardware
primitives are supplied. These tests do not qualify physical display timing,
RF delivery or input on the installed device.

## Package identity

- Payload: 160 bytes, SHA256
  `3576acc0a74f8d2e4d93c645a02bf30d71b899771045236d200b4837d7863371`.
- Resource: 134112 bytes, SHA256
  `859d51274e24b95383a61776c3467929f9aec96bc8ec24cc8f05911635b37168`.
- Updater: SHA256
  `dc64d5d631c0a2eada689f37495efa4a2aa07b0fed4f363e24d7bfc0ecaf0b3a`.

The resource fits the original 134140-byte BIN slot and 33-page program length.
Exact x86 package verification passed: zero resource overread, FF padding,
every PE header, all other 748 resources and the original erase-size instruction
preserved. The program interval is `[0x2B00A,0x4C00A)` and the erase interval
remains `[0x2B000,0x51000)`. Manufacturer binaries and generated
packages stay private. A full installed-firmware dump and independent recovery
remain unverified.

The radio sample was 9000. DG4 is prepared offline to also expose the last
accepted report ID through idle; it has not been installed. See
[DG4 diagnostic](DG4-RADIO-DIAGNOSTIC.md).
The normal HUB radio OLED guard stays enabled. Offline EP2 tests supply their
completion callback; they do not prove that the physical endpoint or Windows
delivered it during this failure.
