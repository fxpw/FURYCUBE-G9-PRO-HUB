# Receiver configuration reads

Updated 2026-11-20. The Windows helper now opens the FA60 configuration
interface for asynchronous I/O. It sends a 134-byte feature buffer and reads
with an explicit input and output buffer. A full settings read on DG4 passed
with six records matching the wired backup. The [transport comparison](OEM-FIRST-RADIO-READ.md)
records the controlled trials.

## The short-response failure

The G9 receiver (`1D57:FA60`) accepts an eight-byte A0 query/ACK, but its prepared
configuration reply uses a **64-byte USB transfer**. The requested logical length
inside A0 remains unchanged: profile/version records are eight bytes; key tables
are 59 bytes. Requesting GET0C with an eight-byte USB buffer caused Win32 error 87.

`G9Wire.ConfigurationReadLength` now separates these lengths. The receiver reply
is consumed once and trimmed to the known logical length before strict header,
profile, complement and checksum decoding. Trailing transport bytes can contain
old data and are neither required to be zero nor interpreted as another reply.
The wired lengths and eight-byte A0 ACK remain unchanged. Unsupported lengths are
rejected before preparing a query; there is no automatic alternate-length retry.

Physical checks on the development unit passed: receiver profile 1/count 5,
59-byte key table including the existing screen-button key, and the OL4 version
marker. The equivalent wired profile read passed. A complete wired settings
snapshot was saved for subsequent comparison. These checks did not write settings.

## Settings records, without an invented complete profile

The receiver's 134-byte collection maximum does not establish that GET0A supports
a 134-byte transfer. One bounded attempt was rejected and stopped. Full receiver
GET0A remains disabled; the normal client does not drain or retry that failed
transaction. Replugging the receiver and then switching the mouse off/on did not
clear the pending response. In a separate one-off diagnostic, one GET0A64 read
returned the expected `0A 78 01` prefix of the previously recorded request and
cleared A0 readiness. No prepare/SET was resent. This recovery is tied to the
known interrupted request and is not an automatic stale-response drain.

The new receiver settings path reads GET04 (sensor/DPI), GET06 (polling), GET07
(profile cross-check) and GET08 (keys), with GET0C before/after. Sensor, polling and
keys are read twice and must match. All logical checksums/complements still apply.
The resulting source is `FirmwareSettingsRecords`, with `fullBodyHex: null` and
`verificationScope: settings-records`. Its archive contains only actual records;
it does not create `logical-profile-117.bin` or invent the unknown alignment byte.

Narrow sensor/polling edits retain every other byte in their known SET body.
They save the baseline before SET and compare the independent readback, key table
and target identity afterward. A mismatch aborts without retry/reset. Onboard
profile-pair recovery from a saved full snapshot remains restricted to USB.

## Power and debounce remain wired

The original firmware's GET dispatcher at `0x33B84` has a ROM switch table at
`0x33BA6`. The GET05 entry leads directly to `0x33C9A`, the shared completion path,
without copying light/power settings. GET04, GET06 and GET08 select actual copy
branches. This was checked against the previously identified stock normalized
image hash; it is an instruction-path finding, not a successful GET05 device read.

Consequently the radio path never queries GET05 or substitutes an OEM host preset.
Power timers and debounce are unknown/disabled in that mode and need USB. The
UI displays a dash for unknown values instead of a numeric zero. Extending those
reads requires separate transport/firmware work; the existence of the old HUB's
controls does not establish fresh readback of every field.

## Validation boundary

Local checks: 61 protocol fixtures, 280 transaction checks, 2,540 settings-codec
checks, 60 editor checks, 69 startup checks and 14 Vue/Edge tests passed. The
settings transactions and browser tests use synthetic transports.

Physical receiver qualification on the development unit then passed:

- Sensor, polling, key and profile records matched the saved complete USB baseline.
- The CLI changed the polling setting from 1000 to 500, checked readback, and
  restored 1000. All six archived records matched the original radio baseline.
- Vue startup automatically read the radio settings, displayed profile 1 and OL4,
  and enabled available DPI/sensor controls. Unknown debounce was displayed without
  a numeric input.
- Applying 500 from the Vue interface reached the real native backend and returned
  a fresh 500 readback. Restoration to 1000 used the CLI after a normal HUB exit;
  the six original records matched again. A fresh Vue startup read matched them too.

The user subsequently confirmed normal cursor and button operation with the HUB
open on radio. The passive G9 counter recorded 1,506 movement packets, seven left
button presses and one right button press. No screen-button key event was captured
in that interval, so it is not an additional physical check of that output.

At the user's request, the existing enabled 1600-DPI level (stage 2) was selected
through the receiver and verified by readback. All other field values, polling,
keys and profile metadata were preserved. This is the new intended current state;
the earlier stage-6 USB/radio baseline is historical and must not overwrite it.

These are configuration-byte/readback checks, not a measurement of actual packet
frequency or physical validation of every sensor/DPI setter. Power and debounce
over radio, radio OLED and last-frame retention are not covered here.

See also [read handshake and freshness](READ-HANDSHAKE.md) and
[settings inventory](SETTINGS-PARITY.md). No new firmware was installed here.
