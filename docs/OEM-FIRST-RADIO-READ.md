# OEM normal receiver startup reads

Updated 2026-11-17. The pinned native application starts normal G9 radio settings
reads with **GET06**, not GET0B. A controlled DG4 test of that exact first query
passed through the original HIDAPI. This establishes a working receiver
configuration read on that session; it does not explain every earlier timeout.

## Exact stock route

The inspected local `FURYCUBE.exe` SHA256 is
`8ca24d9ed79a27b145a7c52a4d0dcc10e0bf1090c27af2646b6cff0be008cc55`.
The original x86 HIDAPI SHA256 is
`f4e23fe7182822b57305a8aee056a38e875c047e0785e598c1cfd2c1ab7a92bd`.
Addresses below are preferred native image addresses.

| Stage | Native evidence | Exact behavior |
|---|---|---|
| Discover receiver | `40D800..40DC93` | Enumerates `1D57:FA60`; opens page000B/usage0 configuration and separate page000A/usage0 input collections. |
| Input startup | `40DB04..40DB22` | Sets host nonblocking mode and starts the input worker. No configuration send/get or relay-enable call was reached in the bounded discovery fixture. |
| Associate G9 model | `40DDE5..40DE74`, `40DE90..40DEE8` | A matching incoming C9 notification associates the feature/input handles; copies host metadata and sets host nonblocking mode. |
| Mark connection | `40E13E..40E174`, `41F9D0..41FA8A` | Sets host model field `+420`, updates UI and posts host messages. This is not a proved receiver-enable command. |
| First settings prepare | `41FF65..41FF6D` | Calls `420300(report=06,length=9,selector=1)`. |
| Normal radio send | `42043F..4204B4` | Constructs `A0 06 09 00 01 00 00 00`; HIDAPI pads the logical eight bytes to feature capacity 134. |
| A0 status | `42050A` through `420240` | Reads eight-byte A0 status. The diagnostic allows a payload read only after ready1. |
| First settings payload | `41FF95..41FF98`, `4202C2..4202D5` | Calls HIDAPI GET06 with **9 bytes**, without payload padding. |

The normal startup prepares directly following GET06 are GET04 and GET05:

| Report | Prepare bytes | Payload GET length | Native call |
|---|---|---:|---|
| Polling06 | `A0 06 09 00 01 00 00 00` | 9 | `41FF6D`, payload `41FF98` |
| Sensor04 | `A0 04 38 00 01 00 00 00` | 56 | `42001C`, payload `42004B` |
| Lighting05 | `A0 05 0F 00 01 00 00 00` | 15 | `4200ED`, payload `420118` |

GET05 has no useful stock G9 data handler and is **not** a proposed probe. The
unmodified native program's sequence is evidence, not an allowlist for copying
every operation. Native startup includes retry loops; our diagnostic sends each
approved prepare once.

All six direct calls to `420300` in the pinned code are the three entries above
and a corresponding sequence at `42EB3D`, `42EBEC`, `42ECBD` for another model.
No G9 GET0B was found in this startup path. This does not establish that GET0B is
universally unsupported or wired-only: earlier direct receiver GET0B captures
succeeded. It means that repeated first-query GET0B failures did not reproduce
the native program's first read. Likewise, historical success with 64-byte
payload reads does not make 64 the native GET06/GET04 length.

## Observed DG4 GET06 result

After the owner reset the radio connection, enumeration showed only FA60, the
OLED baseline was 1000. A separate DG4 radio input check had already passed
9/9 before the preceding version-query session. The reviewed private
original-HIDAPI probe then logged:

```text
A0 GET8: A0 00 00 00 00 00 00 00
send logical8: A0 06 09 00 01 00 00 00, return8
A0 GET8: A0 01 00 00 00 00 00 00
GET06 length9: 06 09 01 44 BB 00 00 00 00
```

The response exactly matched the saved profile 1 / 1000 Hz record. The owner
then observed **1096** after movement and stop and confirmed normal cursor and
buttons. On DG4, 1096 decodes to report 0x06/state 0.
The process
exited with code 0 in approximately 1.6 seconds. No settings/OLED write, prepare retry or
ungranted payload read occurred. The caller retained an external process
deadline because the original library's synchronous wait can block.

Evidence is under
`evidence/flash-experiment/20261116T2305457438502Z-DG4-bounded/oem-polling-probe/polling.log`.
This result combines a different report with exact OEM lengths and the original
library; it does not isolate which difference matters. A prepared private
managed `G9Connection.QuerySettings(6,1)` control can isolate the existing
8-byte prepare/64-byte payload transport from the OEM padded 134-byte
prepare/9-byte payload combination, without preceding firmware/profile reads.
After another owner-confirmed reset, the baseline was 1001 (report 0/state 1).
That managed control sent one unpadded eight-byte GET06 prepare and timed out
with A0 not ready, before requesting any payload. After movement and stop the
owner again saw 1001. Changing only startup order was therefore insufficient
in this comparison. The 64-byte payload path was not exercised by the failure.

A separate control is prepared to change only the native SET buffer to 134
bytes, leaving its A0 reads, open mode and granted 64-byte payload read intact.
It has not run yet. This does not yet prove a complete radio settings read or
OLED transport.

The Vue host now skips its separate automatic firmware-version query when the
selected device is on radio. That query previously ran after a successful
settings scan, both from the timer and the manual scan action, and could stall
the otherwise usable channel with GET0B. The generation is marked as attempted
and the radio firmware value remains unknown; wired version reads are unchanged.
The native Vue host compiled successfully. This source change has not yet been
checked in a live HUB session and does not change the configuration transport.

## Exact next sensor-read boundary

The native GET04 requests 56 bytes, whereas the existing logical sensor decoder
expects 52. The stock worker follows the requested length. For selector1 its
native 56-byte response is:

| Offsets | Meaning |
|---|---|
| 0..2 | `04 38 01` |
| 3..49 | 47 sensor-setting bytes |
| 50..51 | Big-endian sum of those 47 bytes |
| 52..55 | Four zero bytes |

An exact native probe should preserve all 56 raw bytes, compare the body with
the saved profile 1 record, verify its checksum and require those four trailing
zeros. The existing 52-byte decoder must not be fed the `04 38 01` frame
unchanged, nor should production validation be loosened to accept it by length
alone. A private exact 56-byte check can keep that transport experiment separate.

Both GET06 and GET04 with selector 1 perform selected-profile preload work and the
common active-profile reload after constructing the response. Bounded offline
execution with valid synthetic profile 1 records found unchanged configuration
and no NVM/global-profile writes. These reads can still change runtime state
through profile loading; they are not neutral RAM observations. After the
managed GET06 comparison, a separate guarded native GET04 with an external
deadline can qualify the next useful record. The diagnostic does not
automatically continue through the entire native sequence.

## Offline proof and limits

Private reproducible scripts beside the DG4 experiment:

- `audit-oem-receiver-init.py` / `oem-receiver-init-offline.json`: actual discovery
  instructions, five supplied enumeration cases; OS/HID/string boundaries
  supplied and newly created input threads not scheduled.
- `audit-oem-first-polling.py` / `oem-first-polling-offline.json`: actual native
  prepare/payload helpers plus original HIDAPI for GET06/GET04; supplied ready
  ACK and sensor reply. Stock/OL7 complete mouse worker execution independently
  proves reply shapes, profile-load paths and absence of NVM writes in the
  valid-record fixtures. DG4 preservation is covered by its separate candidate
  suite.
- `native-prepare-direct-callers.json`: all six direct prepare callsites.

These scripts hash-check their local OEM inputs and perform no device access or
compilation. No receiver firmware was emulated. No hidden device-enable command
was found within the inspected discovery, handle-association, connection and
normal prepare paths; this is a bounded result, not a trace of the whole OEM
application or a physical diagnosis.
