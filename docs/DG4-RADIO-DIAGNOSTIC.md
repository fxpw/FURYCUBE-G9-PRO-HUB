# DG4: last accepted request and RF state

Updated 2026-11-17. **Installed; USB input passed 9/9 after the approved
Interception recovery, without a PC reboot.** This is a temporary diagnostic
control without custom OLED image support. On the preceding DG3 build, radio
input passed 9/9, an ordinary version query timed out and the owner observed
9000. That idle sample could not distinguish no reception from completion,
abort or a stale display.

The DG4 updater completed 33/33 blocks and closed normally. A fresh USB profile
backup matched the expected 117 bytes; only the reset screen-key assignment
needed restoration. An independent complete profile reread then matched every
saved byte (profile 1, 1600 DPI, 1000 Hz, screen key 0). The owner visibly saw
1128, consistent with report 0x08/state 0, and reported missing mouse input.
Interception had reattached to G9's USB stack. The previously authorized,
temporarily restored class-registration bypass rebuilt only G9 without that
filter; the fresh passive USB checklist passed 9/9. The original global
registration was independently verified as restored.

After an owner-confirmed radio reset, the visible baseline was 1000 and the
separate radio input check passed 9/9. One GET0B through the native client timed
out before a payload read; after movement the owner still observed 1000. This
did not visibly establish acceptance of that request. The display limits below
still apply.

A later fresh reset and 1000 baseline preceded one **stock-startup GET06**
through the pinned original x86 HIDAPI. It succeeded: A0 became ready, GET06
requested and returned exactly nine bytes, and `06090144bb00000000` matched
the saved profile-1 polling record byte for byte. No settings or OLED frame was
written. The owner then observed **1096** and confirmed continued normal input.
After another fresh reset, the same GET06 through the unmodified managed
transport timed out before a payload read; its visible baseline/result both
remained 1001. A control changing only native SET padding is prepared.

DG4 supplies the native DPI renderer with:

`1000 + (lastReport << 4) + (RFstate & 15)`

The real sensitivity remains 1600 DPI. The original `dpi` suffix remains visible,
but the displayed number is diagnostic. Decode `value - 1000`: the upper bits
are the report ID and the lower four bits are the RF state.

| Example | Meaning at the last native redraw |
| --- | --- |
| 1000 | Report 0 and state 0; expected after fresh initialization. |
| 1176 | Report 0x0B (version), state 0. A retained version context through idle. |
| 1183 | Report 0x0B, state 7 (GET prefix parsed). |
| 1188 | Report 0x0B, state 12 (reply preparation). |
| 1190 | Report 0x0B, state 14 (fragment transmission wait). |

This is a sample of shared native context, not a counter or transaction history.
A later control request or reset may replace it. **USB and radio share this
context:** 1176 could also remain from a wired version query. Before the radio
probe, record a visible baseline with a different report ID and keep the HUB
closed. Only a change from that baseline, with no intervening configuration
operations, can identify the newly accepted request. If the baseline already
contains report 0x0B, 1176 after GET0B is inconclusive.

States of 16 or higher alias
through the four-bit mask. Stale redraws remain possible; the receiver's USB
ready flag is not shown. The stock display policy and activity countdown still
control when the row is drawn.

The first 80 bytes of DG2's extension are unchanged. Relative to DG3, the scalar
also reads the retained report ID, and the wake wrapper marks the DPI row dirty
after any accepted GET ACK rather than only GET0B/length8. Native wake runs
first; AF1/AF2 guards still apply. No SET dirty trigger, direct parser draw, new
RAM/NVM, timer rearm or RF protocol change is added.

The sealed image passed inherited endpoint, normal-read, keyboard and RF checks,
30 real formatter/font/GPIO values, 18 complete native RF-query lifetimes and
host A0/context-preservation checks. Hardware primitives and endpoint completion
are modeled, so these results do not qualify physical radio delivery.

- Payload: 160 bytes, SHA256
  `68b649a99d2d935e7941a742e3dacaa81f9bb07a7fb0d48bf525be94ab8ba5d0`.
- Resource: 134112 bytes, SHA256
  `5b5bad4b0c15a60af976f6b96a5d1d912d1a0d6af6c26587ace0aed0f40b0074`.
- Updater: SHA256
  `e2c6f5135ac8b72b40e737088517173501122629b658307fb59043d74439f24c`.

Exact x86 verification passed, including an independent rerun: original BIN
slot, all PE headers, 748 other resources and original erase instructions are
preserved. The copy fix prevents resource overread and fills transfer padding
with FF. Program: 33 pages `[0x2B00A,0x4C00A)`; erase remains
`[0x2B000,0x51000)`. Generated packages and manufacturer binaries stay private.
Independent full-firmware recovery remains unverified.

Independent review also reproduced an inherited OEM updater limitation: its
host-side success path checks response prefixes but accepts erroneous erase/
program status bytes and an altered verify tail. Therefore 33/33 progress and
the offline transfer proof do not establish installed flash integrity or a
working recovery path. Configuration readback and physical input/display checks
are separate evidence; they do not replace a complete flash dump either.

Next: isolate native SET padding while keeping the managed read path unchanged.
Production transport has not changed. Before any diagnostic whose
interpretation depends on request identity, record a different visible baseline
and observe the result before another configuration operation replaces context.
A passive input check sends no such requests.
See [Windows input recovery](WINDOWS-INPUT-RECOVERY.md) before rebuilding the
USB device stack on the development PC. Receiver image upload remains guarded.
