# Firmware validation: remaining gaps and next physical checks

Updated: 2026-11-09. This is an independent **offline** review. No USB command,
new feature probe, loader transition, updater execution or firmware installation
was performed. The two-byte fix and OL3 candidate are unchanged by this review.
An FCC ID is not a prerequisite; no label search is blocking this work.

The original OLED-button delay has strong stock-device evidence, but the
candidate is **not a complete input/display validation**. Two additional input
lifecycle limitations were reproduced below. Independent recovery and the
current backup/equivalent exact restoration remain mandatory before any
installation; see [RECOVERY.md](RECOVERY.md).

## New findings, beyond the earlier ready-endpoint fixtures

`tools/emulate_button_lifecycle_gaps.py` executes stock gesture, debounced-event,
key press/release and keyboard assembly instructions, with and without the
two-byte no-wait patch. It uses the actual SET0C/configuration-worker paths with
synthetic global/profile flash records. Existing ROM/FIFO/display boundaries
remain explicit stubs; endpoint readiness and debounce transitions are supplied,
not measured. The keyboard no-send branch has a proper synthetic caller frame.

Fourteen scenarios were checked. Six variants expose two **shared stock
limitations**, observed both with ordinary0 on stock middle click and with
ordinary0 on the patched screen button:

1. **Down can be lost under endpoint backpressure.** If a complete debounced
   press/release occurs before the first keyboard report can be accepted, the
   eventual output is only `00 00 00 00 00 00 00 00`. No usage27 down report is
   produced. The phase machine `0x32B30` waits for the prior dirty report to
   clear, while the release path can cancel the unfinished key sequence.
   The fixture sets both EP-ready `0x400B22=0` and `0x400AAF=1` to avoid the
   stock alternate-send branch at `0x38C5E..0x38C64`. This establishes a code
   path, not its incidence or duration on real hardware.
2. **Changing profiles while a mapped key is held can lose its release.** After
   a usage27 down report, a real profile SET/worker load replaces that button's
   assignment with usage28 or a DPI action. Physical release is then decoded
   using the new assignment. The old usage27 remains in RAM `0x400B42..0x400B48`
   and no empty keyboard report follows. For the different-key case the trace
   ends `empty, usage27, usage27`; for the DPI case it ends `empty, usage27`.
   The release dispatcher starts at `0x320D2`, reads the current button table at
   `0x320EE..0x320FE`, and removes a supplied current usage through `0x32092`.
   The synthetic profile load does not remember the old per-button assignment.

These are not evidence that the NOP introduced a new bug. The stock middle
control reproduces both. They are evidence that removing the OLED hold gate
does not by itself guarantee complete key lifecycles under load/profile change.

The same new fixtures successfully checked five repeated taps, a long held
ordinary key, delayed delivery of **an already pending key-up**, and switching
to a profile with the same keyboard assignment. The long-hold check is firmware
report behavior; Windows typematic repetition is outside the model.

Local results: `evidence/button-lifecycle-gaps.json`. Normal invocation reports
the findings; `--strict` exits nonzero while any known lifecycle limitation is
present. This is deliberately not another claim that the whole firmware passes.

## Model correction, with a bounded effect on old evidence

The earlier `emulate_screen_key.py` models ROM `0x24FEC` as a three-argument
memset. The established contract is two-argument zero-fill `(destination,count)`.
The new fixture uses the correct contract and records calls. A focused coverage
check confirmed that the earlier successful ordinary screen-key tap did **not**
reach this stub, so that particular result is not invalidated. Do not reuse the
old stub for new profile/macro/initialization coverage. Its `sensor_calls` name
also counts `0x31A0C`, which is a DPI notification formatter, not proof that SPI
sensor application succeeded.

This review changes only the new proof file and this document. A correction to
the older shared fixture or any firmware lifecycle policy needs a separate
reviewed change; neither was silently applied here.

A subsequent, separate [keyboard-release prototype](KEYBOARD-RELEASE-PROTOTYPE.md)
now implements one proposed policy in new own Thumb source and emulated memory
only. Its 36-case proof reproduces six stock held-key failures, releases them
with the prototype, and explicitly retains the two short-tap/backpressure gaps.
It is not integrated into the published OL3 image and generates no installer.

## Current evidence matrix

| Scenario | Established evidence | Remaining limit / acceptance condition |
|---|---|---|
| Stock failure on the sixth button | Physical stock0 fails alone, works on middle and with right held; raw keyboard events distinguish the device | No candidate is installed; repeat on candidate only after recovery |
| No-wait normal press/release | Original instruction fixtures produce empty/down/up on screen on/off, middle and right chord | Real GPIO/debounce, USB timing and OS delivery unmeasured |
| Repeated/held ordinary0 | New correct-contract fixtures preserve complete edges with ready endpoint | Test tap counts and final release on hardware; allow normal OS autorepeat while held |
| Busy endpoint, short tap | New stock and patched fixtures lose the down event | Additional offline input-sequence policy and measured streaming/load behavior required |
| Busy endpoint after down | New fixture retains pending key-up and sends it when readiness returns | Verify disconnect/sleep and actual USB completion timing separately |
| Profile/max-profile metadata | OL3 tests execute original global load, legacy read/restore, profile SET and worker for all five indexes | Synthetic flash/persistence; no physical preservation test or recovery proof |
| Profile switch while held | New same-binding case succeeds; different key/DPI cases leave old0 held | Offline release policy needed before asking the user to exercise the known sticking case |
| OLED long-hold gesture | NOP intentionally prevents arming AF1, while modeled DPI click still changes stage | The long-hold OLED toggle is intentionally removed; ordinary sleep/wake remains separate |
| First key after real sleep | Active-state and OLED-off-flag fixtures exist | OLED dark is not equivalent to MCU sleep; first-wake key behavior has not been proven |
| Cable/2.4G/Bluetooth transitions | Common mouse-side handler and captured stock wired/radio behavior inform the diagnosis | Receiver firmware, pairing persistence, transport changes and BLE output are not emulated as complete systems |
| GO2 pixel layout/upload | Real native draw instructions and CRC/length/page rejection execute offline | Display orientation, electrical initialization, latency and visible pixels unverified |
| OL3 configured DPI | Complete A0/status flow and profile histories execute offline without custom profile/NVM writes | Source is configured runtime OLED data, not applied-sensor metrology; physical GET22 not tested |
| Static image after stock events | Independent redraw audit identifies direct, dirty-flag and sleep clearing paths | No display ownership/framebuffer/persistence; mixed stock/custom pixels and loss after sleep are expected |
| Live widgets with input | Same-handle status→page→status control flow executes offline | Bit-banged I2C/main-loop occupancy and lost taps under real load need measurement |
| Installation/rollback | Candidate hashes, CRC blocks and unchanged aligned transfer range are verified | Full current backup and app-independent restoration are not established; installation stays blocked |

## OLED frame lifetime is a concrete limitation

[OLED-REDRAW.md](OLED-REDRAW.md) shows event-driven stock drawing, not an
unconditional full refresh for each mouse movement. Ordinary clean-state
clicks therefore do not establish that every custom frame is immediately lost.
Nevertheless battery, DPI/configuration, connection and wake paths overwrite
individual fields, including direct calls which bypass the dirty dispatcher.
Sleep/off paths explicitly send480 zero pixel bytes before display off.

OL2/OL3 have no ownership flag, retained framebuffer or flash picture storage.
The off flag AF2 does not suppress every renderer; AF1 caused the original key
problem and must not be reused for ownership. A static picture cannot currently
be promised after HUB exit, stock redraw or sleep/wake. Live resend can repaint
it while the HUB runs, but does not prove ownership or negligible input latency.

## Next offline work before widening the firmware claim

1. Define release semantics for a binding/profile change while held. A possible
   policy is explicit release of this device's virtual keys before replacing a
   binding, or retention of the assignment used for the original down. Neither
   policy was part of the initial audit. The separate 108-byte prototype now
   checks explicit release on profile load/successful SET08, without new globals;
   it still needs review of its intentional release on legacy settings reload.
   It is not part of the published candidate. See the linked proof and limits.
2. Define delivery of a complete short key sequence under backpressure. The
   pending down must not disappear merely because release arrived. A real queue
   or an audited state-machine extension needs bounds, multi-button ordering,
   modifier and disconnect regression checks. Adding a host mapper is not a
   native firmware fix.
3. Keep the existing no-wait/OL3 payload pinned while assessing these policies.
   Any new candidate needs its own hashes, exact changes, whole-container CRC
   verification and tests on the actual reconstructed bytes.
4. If durable custom OLED ownership becomes a goal, specify stock-mode restore,
   sleep/blank/wake behavior and retained storage separately. Clearing dirty
   flags or setting AF2 is not a complete implementation.

## Physical sequence: only after the recovery gate is satisfied

This is a future sequence, **not instructions to install now**. No user action
is requested while recovery/current backup remains unproven. The missing FCC ID
does not change the gate or justify random key combinations/loader probes.

Before installation, record exact hardware/installed-image identity, a complete
verified backup or equivalent exact restoration, and an independently reachable
recovery procedure. Preserve pairing/calibration/security state, not just the
downloaded app image. Current logical settings exports are useful additional
records, but are not a firmware backup. Avoid using the only working mouse for
destructive recovery validation; a manufacturer-proven exact procedure or an
appropriate independent validation route is needed.

After that gate and a separately reviewed installation, the user's participation
would be limited to these observable checks, with a primary keyboard available:

1. **Native0 first, HUB closed.** In a text field, tap the screen button several
   times, compare with an ordinary control button, then hold/release it. Check
   actual final release; a long hold may legitimately produce OS repeats. Repeat
   in the browser/editor originally affected. No macro test comes before this.
2. **Sleep/wake separately.** Close streaming/status polling so it cannot keep
   the mouse awake. Let the configured idle behavior occur, then test the first
   press and subsequent presses. Record whether the MCU slept or only OLED went
   dark; these are different cases.
3. **Existing radio link.** Confirm the existing2.4G pairing and native0 after
   removing/reconnecting the cable. Do not install a receiver image, re-pair or
   change an unknown boot mode as part of this check. OL3 status/images remain
   wired-only and must be refused on the receiver handle.
4. **Profile preservation with all buttons released.** Use explicit profile
   changes, check the intended profile/count and saved assignments/DPI, then
   restore the starting selection. The known held-key/profile case stays out
   of user testing until the offline lifecycle policy above is resolved.
5. **Visible OLED pattern, then load.** Send a labelled orientation/edge pattern,
   verify clipping and all four pages, then observe DPI/battery/wake redraw and
   sleep clearing. With live widgets, repeat short taps and release checks.
   Missing events or delayed release stop the test; do not increase global
   debounce or overwrite settings as an automatic workaround.

Record device identity, candidate hash, exact starting settings, G9-only input
events and visible outcomes. Keyboard report byte traces are only claimed when
actual byte capture exists; the earlier ETW completion trace did not provide
packet payload bytes. Restore saved settings through reviewed named operations;
no blanket SET0A reset or firmware/updater experiment is a cleanup step.
