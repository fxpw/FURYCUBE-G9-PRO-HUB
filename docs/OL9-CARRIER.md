# OL9 checksum-compatible SET04 carrier

OL9 is a separate mouse-side experiment derived from immutable OL7. It preserves the ordinary SET04 sensor checksum location and calculation while carrying forty OLED bytes in the same 56-byte RF report. It does not establish that the receiver will relay this envelope, and must never be sent to an unmodified mouse. The checksum shape matches `MouseSettingsCodec.BuildSetPacket` in `src/G9MouseSettings.cs`; this envelope is deliberately not valid native sensor settings (stage0 and custom tail).

## Packet

All offsets refer to the complete56-byte packet. The stock RF body excludes reportID/length and travels as30+24 bytes.

| Offset | Meaning |
| --- | --- |
| 0..2 | `04 38 01` (SET04,56 bytes,profile1) |
| 3..5 | Fixed `00 01 3F` |
| 6 | Page0..3 |
| 7 | X0,40,80 |
| 8 | Initialize0/1;1 only at page0/X0 |
| 9 | Count40 |
| 10..23 | Pixel0..13 |
| 24 | Invalid native DPI stage0; claims custom handler |
| 25..49 | Pixel14..38 |
| 50..51 | OEM unsigned sum of bytes3..49, big endian |
| 52..53 | CRC16/CCITT-FALSE over bytes0..51 followed by pixel54, little endian |
| 54 | Pixel39 |
| 55 | Revision9; independently claims custom handler |

The RF hook claims stage0 OR revision9 before the native setter. It then requires report04, exact RF RAM body pointer, length56, context kind0, RF state9, remaining0, sequence2, fixed fields, stage0, revision9, correct OEM sum and custom CRC, and both active/requested profile1. The common GO4 validator enforces page, X, count, initialization placement, and last-page pixel high-nibble rules.

CRC validation saves the expected16-bit value, copies pixel54 into byte52, and invokes the seed-resetting contiguous CRC routine once over53 bytes. Reconstruction first preserves header fields, copies pixel54 to51, moves25..49 to26..50 backward, then moves10..23 to12..25 backward. Only then does it write the normal64-byte GO4 header, tail, and CRC for the shared staging/store/renderer.

All OL7 wired callbacks, EP2 settings guard, NoWait patch, native pixel ownership, sleep policy, retention store, legacy dispatcher and RF prefix restrictions are retained. Version marker/symbols are OL9. Normal captured SET04 with nonzero sensor flags takes the original dispatcher with the same register arguments.

## Artifacts and checks

- New source: `firmware/g9_ol9.S`, `tools/emulate_ol9_carrier.py`, `tools/build_ol9_candidate.py`.
- Payload2080 bytes, SHA256 `1162dce22978468529baffe68f1b8a6b9eb868ee0b2c9b59f268a565f3d4f138`.
- Raw sealed resource136152 bytes, SHA256 `385a653c071cf509d376f8efe2c5d96333cfc68ee7a4d13f7f2b35b8a18dab0e`.
- Framing:4004 CRC blocks total,65 appended blocks,28 changed original blocks, no final FF alignment bytes; transfer rounds to34 pages (`0x22000`).
- Full480-byte upload and native twelve-tick rendering, partial-frame retention, malformed fields/checksums/tag/stage, valid-checksum semantic rejects, OEM mismatch with valid custom CRC, legacy register equality, and inherited native/IRQ/settings regressions execute offline in Unicorn.

Run under WSL from this repository:

```sh
tools/pyenv/bin/python tools/emulate_ol9_carrier.py
tools/pyenv/bin/python tools/build_ol9_candidate.py
```

The builder pins the payload and sealed resource independently, verifies original resource/header/block framing and all inherited checks, then creates a uniquely named raw `.CANDIDATE.bin` and manifest under `patch-output/`. Baseline fixture compilation rewrites shared generated `bin/g9_ol5.o` (and settings regression OL6 artifacts), so serialize full builds with other variants.

The subsequent PE packaging review is complete. `tools/ol9_updater_package.py`
pins the resource and resulting updater independently; the resource fits the
original raw section without moving file offsets or mapped pages. It preserves
748 other resources and changes only the reviewed copy-length/erase-length
instructions plus the resource extent headers. `tools/emulate_ol9_updater.py`
executes the exact x86 preparation and 34-page transfer with hardware APIs
stubbed: no resource overread, unchanged erase interval `[0x2B000,0x51000)`.
The disabled updater SHA256 is
`1f7a34bf8ce51cbc9c0be9072b0b49e5e78849260965734539e3b03cb972980c`.

OL9 was installed over USB on 2026-11-20. The radio frame submission left the
physical display at 1600, with input and saved settings verified afterward.
The owner confirmed a visible screen change when the same frame was sent
through USB to the same OL9 installation.
The [candidate matrix](RF-CANDIDATE-MATRIX.md) records the physical test and the
replay of its exact reports. Independent firmware recovery remains unverified.

The earlier, readback-verified DPI change through FA60 passed a 56-byte SET04
buffer to Windows. The unsuccessful OL9 frame used a 134-byte buffer. The
guarded frame CLI's `--logical-feature-length` comparison sent exactly the
same twelve carrier payloads in 56-byte feature calls. The image reappeared
on the OLED after the switch from USB had returned it to 1600. The passive
input check passed 9/9 and all six readable settings records matched the
USB backup.

Production sends 56-byte OLED feature calls with a 500 ms gap after each
fragment. Stream discovery verifies OL9 and active profile 1 once per
receiver session. The mouse checks the active profile in its carrier
handler. Version, profile and sensor reads retain their 134-byte prepare
framing. The HUB renders numeric DPI from the read-back sensor settings
and passive DPI-stage notifications. Periodic frames skip control reads.

## Radio frame loss and partial updates

Host completion only records Windows accepting a SET. The OL9 carrier has
no mouse-to-host acknowledgement of staging, frame commit, or rendering.
Every OLED frame takes twelve 56-byte SETs. Each SET needs an RF prefix,
acknowledgement, and two RF bodies. Mouse-side staging begins at chunk 0 and
publishes only after receiving all twelve chunks in order. An incomplete
upload leaves the previous frame on screen.

`tools/audit_radio_oled_delivery.py` replays the installed embedded OL9 image
and the exact packed frames from a physical trial. Omitting any one of the
twelve chunks preserved the old frame. A complete replay committed and drew
the new frame. When the second RF body was omitted, the native radio
dispatcher stayed in state 9 and ignored the next SET prefix. Channel
rotation did not clear that state. The transaction countdown restored the
idle state after 250 modeled timer services. Receiver timing, actual timer
cadence, and the reason for the physical missed frame were outside the test.

The visible frame and its missed replacement differed in three 40-byte
chunks, with only 14 bytes changed across six contiguous runs. Sending
only those chunks to the installed firmware preserved the old frame. An
illustrative delta would use 35 bytes including a run count and two frame
checksums. That would fit the existing 40-byte OLED payload. It needs a
separate operation, support on both ends, and a verified base frame.
The mouse would have to apply changes to its staging buffer and publish
only when the resulting checksum matches. The full-frame operation would
still be needed to establish the base and recover from a mismatch.

```sh
tools/pyenv/bin/python tools/audit_radio_oled_delivery.py \
  path/to/installed-resource.bin path/to/previous-480.bin path/to/replacement-480.bin
```

The resource must be accompanied by its generated `manifest.json`.

## OLA partial update prototype

The experimental OLA image keeps the OL9 full-frame and USB paths. The
radio carrier with page byte `0xFE`, X and flags zero, carries a 40-byte
delta: two little-endian CRC32/ISO-HDLC values for the 480-byte base and
target, a run count, then runs and zero padding. Each run has a little-endian
16-bit descriptor (offset in the low nine bits, length minus one in the
upper seven bits) and the replacement bytes. Run offsets must be ordered
and non-overlapping.

The firmware requires a committed host-owned base and an idle staging
bank. It checks both CRCs and the hidden pixel bits before publishing the
staged frame. A missing or rejected patch leaves the current frame active.
The normal renderer still refreshes the panel from that committed frame.
The optimization reduces radio commands; the panel draw remains unchanged.

The guarded `G9Device oled-delta` command requires OLA and the
receiver's exact HID path. Its base argument must contain the frame
confirmed on the physical display. Host success still means only Windows
accepted the SET. An oversized delta is reported before any HID access;
the ordinary full-frame command handles that case or a lost base. Live
widgets keep the existing path until delta delivery is qualified on hardware.

Build with `tools/emulate_oled_delta.py` using the existing private
snapshot and two packed 480-byte frames. The emulator validates the radio
dispatch and native OLED output, exact host/Python packet agreement when
given the host packet, common input and settings paths, and startup/retention.
The experimental USB updater was separately emulated over all 34 blocks.
The physical USB installation completed all 34 blocks and returned the OLA
marker. The saved active profile was rechecked after restoring the OLED
button assignment. The user confirmed wired input and the receiver's Raw
Input log recorded motion and ordinary clicks.

The receiver displayed the full control frame. The first delta left that
frame visible. Two individual delta submissions during receiver-input
pauses then changed frame 3 to 4 and frame 4 to 3. One more submission
while the receiver was reporting motion also changed frame 3 to 4. In
that trial the Windows call took 2.9 ms and the adjacent motion events
straddling the call were 383 ms apart. The display was checked visually.
The matching USB response tail describes buffered report data and carries
no display acknowledgement.

An offline regression reproduced the delta branch leaving the native
SET04 profile selector set. The updated candidate clears it for a
checksum-validated delta before checking and applying its payload. Both
acceptance and rejection paths passed the emulator. The installed OLA
predates this cleanup. Radio delivery reliability and the cause of the
input gap remain open.

The installed OLA image retains the stock radio input poll at
`0x35D72`. With an input tick pending and the other gates clear, it
reaches input sampling in radio state 0. State 9, waiting for the second
body of a SET, takes the skip branch before sampling. The offline radio
audit reproduced both branches and verified that sampling becomes
eligible again after the transaction resets. The timer took 250 modeled
ticks to expire. Their physical period and the radio state during the
383 ms gap were not measured.

## OLB shorter receive window

OLB adds one hook to the native 30-byte RF body copy. Once a sequenced
first body identifies an OLED delta, the hook caps the remaining SET
timer at 64 ticks. It retains any lower timer value. The native timeout
still resets the radio. The hook checks the SET context and the delta
header, including the reserved page and invalid sensor stage, and then
calls the original copy routine. Full frame uploads and sensor reports
keep their original timeout. The receiver still sends two RF bodies
and the complete packet still goes through both checksum checks.

Reproduce the offline build with `tools/emulate_oled_delta.py` and
`--short-wait`. The replay compares OLB against OLA on the same frame
pair. If the second body is lost, input polling becomes eligible at
tick 64 on OLB instead of the stock limit of 250 ticks. A second body
on tick 63 still renders the target. A late second body leaves the old
frame intact. These are firmware timer ticks. Measuring end-to-end
latency and delivery rate over the physical receiver requires an A/B
test with continuous input.

The wired OLB install completed all 34 blocks and reported the OLB
marker. The updater reset the screen button binding. After restoring
it from the backup, all eight active profile records and the four
inactive profile records and keymaps matched. The wired input check
passed all nine steps after re-enumerating the mouse. The receiver
input check also passed all nine steps before the OLED comparison.

The wired status query on OLB returned profile 1, stage 2, and 1600 DPI.
The status record has no frame counter or checksum for the current image.

### Comparing motion traces

Use `tools/analyze_radio_oled_motion.py INPUT.csv SEND.log` for both
firmware versions. It reports counts before, during and after the host
call and separates the interval around the call from the largest gap
between actual consecutive reports. It includes the observed baseline
span and the longest later gap. A trace without motion on both sides is
flagged as unbounded. Visual delivery is recorded separately. Raw Input
cannot distinguish a stopped hand from reports the mouse failed to send.

Replaying the archived OLA trace found a 383.2006 ms gap at submission,
zero motion reports during the call, and a later 395.0775 ms gap. The
35 motion reports before submission cover only 37.4367 ms. Use that
sample as a reference observation. For a matched comparison, use the
same packet and procedure with a 1.1-second motion lead-in on both
firmware builds. Keep moving until the probe closes.

On OLB, the owner saw all three expected transitions in the on-device
3 → 4 → 3 → 4 series. The largest adjacent gaps at the three
submissions were 1.53, 38.51 and 8.20 ms. Each run recorded roughly one
second of movement before sending. Two earlier reverse attempts ended
before sending because the trigger required an event rate that the
recorded movement did not meet. The revised trigger checks observed
continuity over 1.1 seconds. The later gaps in the three traces reached
71.46, 91.17 and 68.46 ms. The runs measured motion. Click delivery
during a transfer and a matched OLA repetition remain open.

### Live Vue HUB follow-up

A user saw seconds changing during movement and reported occasional
cursor pauses. A follow-up comparison recorded three left clicks and
one right click, with their releases. Four partial submissions during
recorded motion had adjacent Raw Input gaps of at most 7.01 ms. The
largest gap in that run was 3.30 s. It started 0.93 s before an attempted
full refresh and overlapped ten submitted fragments. Windows Raw Input
can measure that gap. Its start alone cannot identify when the hand
resumed moving.

The experimental worker then tried sending one full-refresh fragment
per update, with the same frame bytes across all twelve requests. The
installed OLB image accepted such spacing in the emulator and kept
input polling eligible between fragments. In the hardware trial the
host submitted all twelve spaced fragments and subsequent deltas,
while the owner reported that the display remained stuck. The
ordinary HUB was restored. Verified on-device acknowledgement of a
committed frame is still required to distinguish successful repair
from another host-only submission.

### Where the partial update spends work

The radio audit can replay the archived delta with `--delta-wire` on
both installed resource images. For the 3 → 4 transition, the OLA and
OLB instruction counts at the four main boundaries were 249 / 59 /
54,106 / 27 and 249 / 115 / 54,109 / 27 respectively. The second body
includes frame validation and commit. The native input poll gate opens
before the renderer begins. Drawing uses 12 loop calls and 98,095 modeled
instructions on both versions, although only three chunks contain changed
pixels. The fixture excludes receiver timing, time between mouse loop
iterations, and time spent waiting for a body. These counts identify
work that can be reduced and do not predict the physical cursor gap.

### Packing nearby changes

The experimental PC encoder now puts changes separated by one unchanged
byte in a single run. The extra byte replaces a two-byte run descriptor.
Both frame checksums and the 40-byte payload limit still apply. The
emulator checks a 29-byte span containing 15 changed bytes that fills
the payload exactly. The archived 3 → 4 wire packet remains the reference
for the OLA/OLB timing comparison.

In each of the two bundled clock and system layouts, a ten-minute
offline trace with synthetic CPU and RAM changes produced 599 changed
frames. The original encoder fit 592 in one packet. The compact encoder
fit 595 and reconstructed each accepted target exactly. With only the
clock changing, all ten changed frames fit with either encoder.

### Selective redraw experiment

`tools/emulate_oled_delta.py --dirty-draw` builds the offline OLC image.
When the previous host frame finished drawing and the screen is active,
the patch compares the validated frame banks and queues only changed
40-byte chunks. The main loop still draws at most one chunk per call.
An unfinished redraw, a full frame upload, warm reset, controller
reinitialization, or a pause while the display is inactive schedules a
full redraw so the retained frame and panel converge.

The archived 3 → 4 delta draws three chunks and executes 24,600 modeled
render instructions on OLC, down from 12 chunks and 98,095 instructions
on OLB. The emulator covered reverse deltas, overlapping updates,
reset, display reinitialization, sleep, and the disabled display state.
The packet bytes and shortened receive window are unchanged from OLB.
OLC has only been built and tested offline.

### Passive frame acknowledgement feasibility

The OLB receiver passively delivered eight five-byte battery input
reports, about two seconds apart, while no feature requests were sent.
The current transport can therefore receive ordinary notifications
independent of the settings transaction.

On the mouse side, `--vendor-envelope` executes the installed OLB's
stock notification branch at `0x35E48` through its send-queue boundary.
The branch copied the four-byte experimental payload into the same
`0x35` RF envelope as a stock battery notification. A queued battery
report was preserved and selected on the next pass. With both slots
empty the code continued to the ordinary-input branch.

The experiment finishes before RF transmission. Whether the receiver
forwards a new command byte to USB remains unverified. The stock
notification slots also run before ordinary input and can be replaced
by subsequent status events. A frame acknowledgement needs its own
queueing and priority rules, and a frame identity that can detect stale
notifications. No acknowledgement command has been sent to hardware.

### OLD acknowledgement probe

`tools/emulate_oled_delta.py --radio-ack` builds a separate image from
OLB. The mouse queues `35 C9 D1 crcLow crcHigh` once per completely drawn
host frame. The CRC is CRC-16/CCITT-FALSE over all 480 packed pixels.
The hook follows the native notification paths and the five ordinary
input checks, so a busy mouse can defer the report until idle. The probe
uses a separate generation field and preserves the native battery slot.

The mouse-side emulator checked a complete frame, a delta, a missing RF
body, delayed rendering, all five input checks, and battery priority.
The restored layout and existing USB/input regressions also passed. A
fresh read-only trace on the installed OLB receiver captured seven stock
battery reports. The wired update to OLD reached 34/34 blocks. The
firmware marker was read back. All eight active-profile records and
the four dormant profiles matched the backup after restoring the
screen key. The wired passive input checklist passed 9/9. Physical
delivery of the new `D1` command over 2.4 GHz is pending.

For the receiver test, capture raw five-byte input reports while sending
two distinct verified frames. Require the matching `03 C9 D1` CRC after
each draw. The 16-bit CRC is a probe identifier. A production stream also
needs freshness and a way to handle missing or delayed confirmations.

OLD advertised its version through the receiver. Windows accepted all
12 fragments in two full-frame attempts, including a retry after three
seconds without desktop input. The display stayed on the previous image
in both attempts. Each capture contained eight battery reports.

After the owner switched the mouse off and back to 2.4 GHz, FA60
forwarded the exact fresh CRC in each of three sequential captures.
The full frame 3 returned `03 C9 D1 4D BE`, the one-packet update to
frame 4 returned `03 C9 D1 BB 17`, and the reverse update returned
`03 C9 D1 4D BE`. The same capture windows contained 8, 11, and 11
ordinary battery reports. The test confirms the receiver path for this
notification. Timing and continuity under motion need a separate live
stream test.

### Confirmed updates in the experimental Vue HUB

Set `G9_HUB_OLED_ACK=1` for the isolated test build. One device process
discovers the firmware once and passively listens for `D1` on the
receiver input collection. The live stream advances from the latest
matched frame CRC. It sends at most one pending update and can split a
large edit across multiple individually confirmed patches. A missing
receipt keeps the last frame during active input. Full recovery yields
to desktop input and backs off after a failed full attempt.

On OLE, the regular HUB enables confirmed updates automatically after
firmware discovery. Older experimental modes still require an opt-in.
Live radio writes pause while a mouse button is held, including retries
and the remaining fragments of a full frame.

The first Vue test confirmed full frames and short updates. The user saw
the previous frame when a large clock change required an interrupted
full upload. The follow-up build replaces that large-change path with
short patches whenever a confirmed base is available. CRC16 correlation
uses the receive window and the sole outstanding frame. A repeated frame
still lacks a device-generated transaction identifier.

In the follow-up Vue trial, the passive trace registered 838 movements
and six button edges. No partial update overlapped that movement span
after a receipt was missed. Once the input paused, the device confirmed
a full recovery frame and 18 of the next 19 patches had confirmed by
the end of the trace. Continuous live updates during movement remain open.

### OLE delta replay

The experimental OLE build lets the hub retransmit its outstanding
delta. If the original command was lost, the retry applies against the
same base. If the device already drew the target, it compares the active
CRC32 and the signature of the last validated patch for that generation.
A match makes the ordinary `D1` receipt eligible again. The existing
notification order still gives input and battery reports priority.

The hub retains exactly one outstanding payload. It makes up to two
single-packet retries with at least 1.2 seconds between attempts before
considering a full refresh in an input pause. The replay policy requires
the OLE marker. The emulator confirms lost-body recovery, duplicate
receipt without redraw, rejection of an altered packet, and expiry of
the replay after a later full commit. The updater programmed all 34 pages
over USB and the mouse reported the OLE marker. All five profiles matched
the backups after restoring the OLED button mapping. The passive USB
input check passed 9 of 9 steps. On 2.4G the mouse returned a D1
receipt for a full baseline, then two D1 receipts with the target
checksum for the same delta sent twice. The Vue clock trial resumed
after retries using only deltas once its full baseline was confirmed.
Raw Input recorded motion during six delta calls and the user saw the
clock updating. The first full-frame receipt took about 48 seconds
across four attempts. The trace cannot distinguish a lost fragment
from a lost receipt for those initial attempts.
