# OL7: normal radio GET audit

Offline audit, 2026-11-16. Normal GET0B/0C preserves the original mouse-side
RF path in the checked instruction model. GET0B adds its intended OL7 marker;
GET0C produces the same bytes as stock. This does not identify the cause of
the current physical receiver timeout.

## Observed failure

`evidence/flash-experiment/20261116T1609236310527Z-OL7-bounded/radio-before-frame.log`
records receiver FA60, idle A0, one `A0 0C 08 00 01 00 00 00` prepare, then
fourteen idle A0 observations. No payload GET0C appears. This preceded every
OL7 OLED frame. The earlier USB `firmware-after.log` returned `0B080101084F4C37`.

The receiver restart record contains code 3010 and a reboot-required message,
with PnP status OK before/after. Receiver transaction reset remains unverified.
This audit made no device requests or changes.

## Instruction paths

The cached `bin/g9_ol7.o` text is exactly 2032 bytes, SHA256
`1c7da04f67a9d40346d2fcf9dc34b2490ef6a41cea47c8837485ce8b98b58290`,
matching the pinned OL7 builder. Original instructions came from
`evidence/g9pro-firmware-normalized.bin`.

| Location | Original behavior | OL7 behavior |
| --- | --- | --- |
| `0x30548` | Call stock RF FIFO reader `0x373AC` | Call prefix shim `0x47BC8`; it calls the same FIFO reader, then discards only report22 prefixes `36/37`. Reports0B/0C return to the original parser unchanged. |
| `0x305FC..0x3060E` | GET prefix becomes ACK47, selector saved, RF state7 | Original bytes unchanged. |
| `0x30146..0x301B2` | ACK completion sets remaining=length-2, sequence0, state11 for selector1 or12 for selector0 | Original bytes unchanged. |
| `0x30032..0x3007A` | Selector1 preparation moves normal GET0B/0C from state11 to12 | Original bytes unchanged; these two report IDs do not queue the sensor/macro preload cases. |
| `0x2FFF8..0x30030` | Wait for pending flags `0x400B2D/2E` to clear, prepare response, call `0x33B84`, set state13 | Same instructions except the GET call targets shim `0x47BC0`. |
| `0x47BC0` | No original extension | Exactly `LDR r3, =0x33B85; BX r3`: tail-call the original dispatcher with the original return address. No report/kind/profile/pointer validation is added here. |
| `0x33C94` | Write stock version bytes | Version hook writes those bytes plus `OL7`; this is the expected GET0B delta. |
| `0x33CAC` and `0x2FF3E` | Build profile/complement reply; fragment response for RF transmission | Original bytes unchanged. |

The carrier SET guard is at the separate `0x3012A` SET callsite. The mouse USB
EP0 readiness guard is at `0x37896`; normal A0 queries retain their original
path. Neither adds a condition to normal mouse-side RF GET0B/0C.

## Offline comparison

No compilation was run. Cached layout hooks were retargeted in memory to the
hash-checked OL7 symbols. Paired `emulate_ol5_radio.RadioFixture` instances used
the same synthetic active-profile1 flash; the stock instance restored every
original hook/data/descriptor byte in emulator memory.

Twelve pairs covered report0B/0C, selector0/1, kind0/1/255 through stock prefix,
ACK/completion, state11-to12 preparation, GET construction and fragmentation.
Legacy GET kind255 acceptance does not qualify a custom SET carrier.

- Prefix/ACK state and the entire 128 KiB application-RAM mapping were identical.
- GET0C RAM and RF bytes were identical; selector1 returned `0C080101FE05FA00`.
- GET0B construction differed only in the three intended marker bytes.
  Selector1 stock was `0B08010108000000`, OL7 was `0B080101084F4C37`.
- Profile-load histories and wake-call counts matched; no flash writes occurred.
- Four further pairs set pending flag `0x400B2D` or `0x400B2E` for report0B/0C.
  Both images remained at state12 without a reply. Neither flag was observed
  on the real mouse.

## What remains unknown

RF FIFO input, ACK interrupts and main-loop scheduling are supplied by the
fixture; transmissions are captured. Receiver firmware, USB-to-RF relay,
physical RF/timing and MCU sleep are outside the model. Wake is a counted
boundary. Stock code calls wake at `0x305D2` and `0x33B86`; that does not prove
the real MCU received the prefix or reached those calls. No live RAM was read.

Idle receiver A0 cannot distinguish an undelivered prefix, sleeping/unserviced
MCU, original pending-work stall or stale receiver transaction. The model found
no normal GET regression in the two RF shims; timing and hardware effects of
the complete installed image remain untested.

## Next diagnostic, when hardware work resumes

Establish completed recovery/reset first; 3010 plus PnP OK is insufficient.
Keep OL7, establish fresh radio input/wake, then attempt one serialized normal
GET0C with the existing bounded wait and stop-on-timeout policy. Prove the
legacy channel works before OLED/new firmware. Do not drain an ungranted
payload, resend the timed-out prepare or loosen the production RF guard.
There is no evidence that OL8/OL9 repairs this existing timeout.

See [radio settings](RADIO-SETTINGS.md) and [read handshake](READ-HANDSHAKE.md)
for the receiver's 64-byte payload transfer and the limits of the A0 ready bit.

## Loop-hook return registers and real configuration worker

Additional offline audit: the original native test stubs `0x355B8`, so its
"called once" result alone does not prove preservation of that service's
behavior. Disassembly and new in-memory comparisons checked the real body.

`g9_ol7_loop_hook` does change original return values in r0-r3 and NZCV. In
particular, stock `0x355B8` saves/restores incoming r3, whereas the shim loads
its target into r3 and then calls a renderer that clobbers r0-r3. Those changes
are real; there is no promise of matching return registers or flags here.

The two audited callers do not consume these values as service results:

- After `0x2EA02`, the next instruction calls `0x35636`, whose independent
  peripheral/timer service reloads its working values and establishes its own
  comparison flags. The actual configuration worker follows at `0x2EA0A`.
- After `0x364FC`, the next instructions call `0x2B42A` and `0x2B7E2`.
  `0x2B42A` reloads r0-r3 before its first conditional branch; the later
  caller's branch at `0x3650A` loads and compares its own RAM value.
  The configuration worker is earlier in this loop at `0x364D2`.
- The `0x36404` main loop explicitly selects wired versus RF service using
  `0x400A7C`. The assembly comment describing the two hooks as one RF and one
  USB callsite is imprecise; `0x364FC` belongs to their shared loop.

Forty-four paired executions restored the original BL only at both loop
callsites in the reference instance. They exercised the **real** `0x355B8`
body under eleven supplied states (disabled service, unavailable/blocked
sample, GPIO high/low, charging flag and countdown 1/2), at both callsites,
with the renderer inactive/active. GPIO register reads were supplied inputs.
Then the actual following `0x35636` plus `0x3136C`, or `0x2B42A` plus
`0x2B7E2`, executed; the next peripheral branch was held stable.

All four return registers differed in some cases; NZCV differed in 28/44.
r4-r11, SP, CPU mode and IRQ/FIQ masks matched. The following instructions
produced identical first 16 KiB application RAM, including configuration and
pending flags, with equal profile-load histories and no flash writes. The
renderer has its expected separate metadata/GPIO effects. This establishes
the tested continuations, not every possible peripheral state or timing.

Four further pairs executed the real RF path through `0x3643A..0x36508`,
including `0x306CC`, its state11/12 dispatch, `0x3136C`, and real `0x355B8`.
Unrelated main-loop services remained explicit fixture boundaries. Starting
with profile-load task `0x22` in B2D took two iterations to reach GET state13;
starting with that task in B2E took three. Both flags cleared in both images,
with equal profile/configuration RAM. Active rendering emitted one chunk per
iteration without preventing the worker from completing.

The original loop has scheduling gates that the hook does not change:
`0x400B0A=0` or `0x400ACA>=2` can branch to `0x36508` before **both** worker
and loop hook; `0x400ACB!=0` jumps to `0x364DE`, skipping the worker for that
iteration. A paired execution with B0A zero reproduced the same unserviced
pending flag in both images. Enabled test iterations explicitly supplied B0A
and held ACA/ACB clear; actual timer interrupts and sleep were not simulated.
No real value of these flags was observed. These original gates are diagnostic
possibilities, not evidence of the physical timeout's cause.

No source/object compilation, device access or firmware changes were made for
this additional audit.
