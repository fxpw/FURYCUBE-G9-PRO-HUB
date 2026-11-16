# Minimal firmware controls for the radio investigation

Updated 2026-11-16. **DG2 is temporarily installed on the development mouse.**
It has no custom OLED support. USB settings and all nine input checks passed.
The first radio version request timed out, while radio input passed 9/9.
This is a diagnostic checkpoint, not completion
of radio OLED support.

## Why use a control image?

OL7 displayed a complete USB image while mouse input continued working. On
radio, two profile reads succeeded, but the following sensor read timed out.
After separate physical resets, both selected-profile and current-record
version requests also timed out. Neither attempt reached image transmission.
The radio input checklist nevertheless passed 9/9.

DG1 and DG2 remove the custom OLED extension to compare an ordinary radio
request with the manufacturer's original radio code. They preserve the
screen-button fix and the USB mouse-endpoint readiness guard. A successful or
failed comparison will narrow the investigation; reset history and hardware
timing still prevent attributing a result to one OLED hook alone.

## Exact scope

| Variant | Original instruction changes | Appended code | Custom OLED |
| --- | --- | ---: | --- |
| DG1 | NoWait NOP at `0x3201C`; EP0 readiness hook at `0x37896` | 36 bytes | None |
| DG2 | DG1 changes plus normal claimed-request metadata hook at `0x342A4` | 80 bytes | None |

Outside these declared patches and the sealed resource header, the original
application bytes remain identical. In particular, the radio parser, response
getter, USB descriptors, main loops, allocator and stock OLED writer are
unchanged. The DG2 metadata hook follows the original zero/nonzero-selector
paths for ordinary reports; it does not add a custom report `0x22` handler.

Both variants retain the stock version response `0b08010108000000`.
**That response cannot uniquely identify DG1 or DG2.** Installation identity
comes from the pinned package and installation record, not its version string.
The native OLED client rejects the stock signature; do not send experimental
OLED packets to these control images.

## Physical observations

- DG1 completed 33/33 updater blocks. USB version, profile, all-data and sensor
  requests returned, but the subsequent polling request returned a stale sensor
  header (`04` instead of `06`). The strict decoder stopped before any settings
  write. Its first all-data reply was retained as an unqualified observation,
  not a complete verified backup.
- DG2 completed 33/33 updater blocks and the updater closed normally. A complete
  USB settings read passed. The updater had reset the screen-button assignment;
  restoring only that entry and independently rereading all settings produced
  an exact match of all 117 logical profile bytes.
- The DG2 USB checklist passed **9/9**, including movement, left/right/middle/
  back/forward clicks, both wheel directions and the screen button's assigned
  `0`. It closed automatically and saved its JSON report.
- After the owner disconnected USB, switched the mouse off for ten seconds,
  enabled 2.4 GHz and replugged the receiver, DG2's first ordinary version
  request (`a00b080001000000`) timed out. Every observed A0 reply was not-ready;
  no response payload was consumed and no image or settings write followed.
  The passive radio input checklist then passed **9/9** and closed automatically.
  Thus the configuration failure also reproduces without the custom OLED
  extension. This does not establish the exact receiver/mouse failure point.
  No image has been submitted on DG1 or DG2.
- A separate reset preceded a version query with the OEM's 134-byte padded
  prepare format; it also timed out. One original-library A0-only observation
  agreed that the response was not ready. A complete original-library query
  probe is prepared but not yet run. See the [API boundary audit](OEM-RECEIVER-FRAMING.md).

The restored profile is profile 1, stage 2 / 1600 DPI, 1000 Hz, with ordinary
key `0` on the screen button. Its 117-byte SHA256 is
`7ffa0a87f04a79e52b4f4a519485f0ddbc0098ca5c9f94c0e8dcf8a2cea8d643`.
This is a settings backup; it does not include a complete firmware dump.

## Offline checks and their limits

The checked decoded images execute actual ARM/Thumb instructions in Unicorn.
ROM helpers, flash storage, FIFOs and hardware events have explicit synthetic
boundaries. These models do not prove physical radio delivery or timing.

Both variants pass 48 endpoint cases, six keyboard-output cases, 512 readiness
leaf cases, 30 comparisons against the stock radio path, and the existing
screen-key/input-path checks. DG2 additionally passes 720 interrupted normal
request comparisons against a serialized reference. Of those, 400 selected-
profile cases explicitly verify that the requested-profile loader ran.

The model reproduces DG1's stale polling header and checks that DG2 returns the
correct polling header for the same interruption. Its synthetic response bytes
are not a device capture; the subsequent complete physical USB read is the
separate hardware validation.

## Packaging

Manufacturer binaries and generated packages remain private and ignored. The
repository contains only this project's patch source and verification tools.

| Variant | Resource bytes | Resource SHA256 |
| --- | ---: | --- |
| DG1 | 134012 | `efeff38a7c75a992140c5fa726367d2c2fbe8064c70b17f3ac41e9e396ebe0da` |
| DG2 | 134044 | `b36bb61bb604d05726789bf1c323ab9e0994972960faa29c10c1a1b65ec20cb3` |

Pinned updater SHA256:

- DG1: `9d892c47b8ef3972393c5d44d3289086efa9f511ab756617c2ade83091094195`
- DG2: `08ebd060ba661a2218a56dadc2476e70075126f6d1c36fbe1c7b59d07f3399ab`

The resource fits inside the original BIN slot and original 33-page program
length. Only the BIN contents, its resource-size DWORD and a 16-byte exact-copy
length correction change in the updater. Every PE header and the 748 other
resources remain unchanged. The manufacturer's erase-size code is unchanged.

Exact x86 preparation and transfer emulation checks zero resource overread,
`FF` padding, erase interval `[0x2B000,0x51000)` and program interval
`[0x2B00A,0x4C00A)`. These are bounded package checks, not independent recovery
proof. A complete installed-firmware dump and a verified emergency rollback
remain unavailable.

The relevant source is `firmware/g9_dg1.S`, `firmware/g9_dg2.S` and the
`build_dg*_candidate.py`, `emulate_dg*_minimal.py`, `dg*_updater_package.py`
and `emulate_dg*_updater.py` tools. They require the pinned private OEM inputs,
Clang and the existing Unicorn/PE tooling; they do not access a device or launch
an updater. They are separate from the ordinary Windows application build.

## Next steps

1. Audit the normal receiver/request path against the vendor host and the
   previously successful OL4 captures. The DG2 comparison has reproduced the
   timeout, so repeating the same request is not the next diagnostic.
2. Prepare a bounded observation that identifies whether the mouse received
   the prefix, prepared a response or stalled on pending work. Keep radio
   OLED blocked and preserve ordinary input and the verified settings.
3. Use that evidence to isolate the failing addition or protocol transaction,
   restore a reviewed OLED-capable build over USB, and qualify one visible
   radio image with working input before enabling ordinary HUB radio uploads.

PC-side cached-frame replay, reconnect handling and widgets remain part of the
goal. Autonomous retention across a device restart is a later task; macros are
deferred at the owner's request.
