# OLED USB OUT packet assembly and OL4

The first physical OL3 image upload failed. The configuration interface then
stopped answering until the owner power-cycled the mouse. After power-cycling,
the OL3 capability marker and a CRC-valid GS3 runtime-status read both worked.
This distinguishes a successful installation/read path from image delivery.

Two independent offline audits reproduced the fault in the original USB OUT
completion instructions at `0x3778E`. A 132-byte SET transfer arrives as
64 + 64 + 4 bytes. The partial-packet path at `0x3795E..0x3797C` copies to an
unchanged destination; the terminal path parses the final packet's stack bytes
as the report header. The intended GO2 header is lost before the OLED handler
can validate it. The former fixture supplied all 132 bytes in one synthetic FIFO
completion, so it did not exercise this hardware packet boundary.

GET uses a different path. The successful 132-byte GS3 read does not validate
132-byte SET. Existing sensor, button and polling writes fit one packet; the
120-byte complete-settings operation is a read. Padding an OLED write to the
HID collection's maximum 262 bytes would make the OUT problem worse.

The initial OL4 avoided changing the stock USB callbacks. The current EP2-guard
version changes exactly four bytes at `0x37896` to call a report-specific guard;
all other original USB callback bytes remain unchanged. Its report-22 descriptor declares
63 payload bytes plus the report ID, and every image command fits exactly one
64-byte packet. Each page is split into three 40-column chunks; a full 120×28
frame uses twelve commands. No new persistent globals or flash-picture storage
are introduced. The native host refuses image writes on OL2/OL3.

## Wire format

| Bytes | GO4 image chunk |
|---|---|
| 0–5 | `22 40 47 4F 04 01` |
| 6 | Zero |
| 7 | Page 0–3 |
| 8 | Starting column 0, 40 or 80 |
| 9 | Pixel-byte count, 40 |
| 10 | Initialize flag, allowed only at page 0 / column 0 |
| 11 | Zero |
| 12–51 | Forty page-packed pixel bytes |
| 52–61 | Zero padding |
| 62–63 | CRC-16/CCITT-FALSE of bytes 0–61, little endian |

The final page's unused upper four bits must be zero. Firmware checks the
complete chunk before waking or drawing. The host validates all twelve chunks
before the first write and stops after the first transport failure.

The capability marker is `OL4`. Runtime status uses
`A0 22 40 00 00 00 00 00`, followed by a 64-byte GS4 response. Its first ten
bytes retain the GS3 status/profile/stage/configured-DPI meanings, with version
4, zero padding through byte 61 and CRC at 62–63. A legacy OL3 status read stays
132 bytes; version and length cannot be mixed.

The regression runs the original OUT completion with actual 64-byte packets
before the main-loop handler, and reproduces the old three-packet failure.
On the development mouse, the owner confirmed the first control image's border
and readable `OLED OK` / `G9 PRO` text after twelve successful submissions.
Current live-widget/input results are recorded below; native redraw and sleep
retention remain separate limitations.
Successful host submissions alone do not prove visible output on another unit.

## Repeated-query metadata race

A separate physical read after GS4 returned `22 08 02 01 08 4F 4C 34` for a
firmware-version request. The client rejected it before sending any image.
The exact bytes were reproduced by injecting the real OUT completion while the
idle main FSM was paused between its metadata loads and pending-flag test.
Registers retained the preceding report `22` and selector `02`, while the USB
interrupt queued the new `0B / 08 / 01` request.

The OL4 prepare hook now reloads report, length and selector after the main FSM
claims and clears that pending flag. It uses the claimed request rather than
the registers sampled before the interrupt. Eight hundred instruction-boundary
cases compare version, status and legacy profile requests with serialized
baselines; disabling the reload reproduces the exact observed failure.

This correction assumes one serialized request owner. It does not authorize
concurrent HUB instances, a second configuration program, or accepting a
malformed response. Identity, CRC, length, selector and cross-record checks
remain mandatory. The initial physically tested OL4 image predates this reload.
The corrected image was subsequently installed on the same mouse; four physical
status-to-version cycles returned CRC-valid GS4 and the correct next version
header. This live check complements the forced-interrupt regression and does
not establish support for multiple concurrent configuration programs.

## EP2 readiness guard and current physical checks

The stock EP0 OUT path sets the mouse-endpoint-ready byte even though endpoint 2
may still be transmitting. The current guard skips this forced-ready store only
for report `22`, including malformed custom packets. Other report IDs retain the
original store, and the real EP2 IN-completion callback remains unchanged. This
is one pinned four-byte hook at `0x37896`, not a replacement USB OUT assembler.
The model exercises 53 FIFO/readiness cases, including mouse buttons, movement,
release and the separate keyboard endpoint.

An earlier live session lost movement and clicks and did not recover merely by
closing the HUB/helper. A Windows reboot restored input. The persistence of that
failure has not been fully explained by the model; do not claim the guard alone
has established its complete cause or recovery.

On the installed 708-byte guard version after that reboot, the owner confirmed
normal input after one 64-byte chunk without initialization, a full initialized
twelve-chunk frame, opening the HUB and a timed ten-second live interval. During
that interval, the actual OLED child process was present; passive G9 movement
count increased by 843 and left-click count by two, while the other-mouse count
did not change. The right-button count did not increase; simultaneous right-click
success was user-reported. After stopping the HUB/helper, the mouse and assigned
key still worked. These are limited current-device results, not long-duration
qualification or an independent recovery procedure. The owner also confirmed a
PNG rendered at 84×20 in position (18,4), with normal cursor and clicks continuing.
The final 2.4 GHz check passed for movement, left/right clicks and the assigned
key, confirmed by the owner and passive input events. The owner observed the
stock screen after changing from cable to radio. This does not isolate whether
disconnect, mode change or another stock path reset the image; cross-mode
retention is unsupported.

The host worker now accepts and validates both GS3/132 and GS4/64 status frames;
175 fake-worker checks cover this transport correction. The owner subsequently
confirmed the numeric-DPI GUI widget on the physical OLED, continuing updates
and normal mouse movement/clicks during the test.
