# Stock OLED redraw and custom-frame lifetime

Offline audit, 2026-11-09. No USB/helper process, firmware write or host change.
Addresses refer to the normalized stock 1.08 image at effective base `0x28790`,
SHA-256 `e0cc9bcb8930ea430d8bc14fefb746f189f0f5f743b81c0b32a5b161f7fba5db`.
The PCB/display controller identity is still unconfirmed; this audit does not
assume a particular OLED datasheet or measured pixel retention.

## Result

Stock rendering is **event/dirty-state driven**, not an unconditional full
refresh for every mouse report. A clean active-state button-handler fixture
does not redraw on ordinary left/right/middle clicks or the patched screen
button assigned to a keyboard key. This does not establish unconditional
retention through the complete sensor/main loop.

Custom pixels nevertheless have no ownership protection. DPI/configuration,
battery/charging, connection and wake paths can overwrite portions of the
image. Existing sleep/off paths explicitly write zeroes to all 480 page bytes
before turning the display off. Merely stopping the HUB therefore does not
make a static picture durable through sleep. This is an uninstalled protocol
prototype limitation, not a physically verified result on the mouse.

## Redraw paths and trigger timing

* `0x3D51E` is a composite stock renderer. It changes contrast and draws the
  header, link/battery information and configured DPI through helpers including
  `0x3D514`, `0x3D37C`, `0x3D372`, `0x3D328`, `0x3D2DE`. It clears several
  pending flags at `0x3D54A..0x3D56C`. It is not itself an unconditional 480-byte
  clear; an existing custom picture can become mixed with stock fields.
* `0x3D856` dispatches pending redraws only when `RAM[0x400AB6] == 0` and
  `RAM[0x400B2F] != 0`. With no pending flags it returns without drawing.
  Its observed flag routes include:

  | Flag | Dispatch site | Stock operation |
  | --- | --- | --- |
  | `0x400A82` | `0x3D868..0x3D888` | Full stock layout via `0x3D51E`; also clear via `0x3D824` when `0x400A8E` is set |
  | `0x400A88` | `0x3D88C..0x3D896` | Battery renderer `0x3D37C` |
  | `0x400A83` | `0x3D89A..0x3D8A4` | Configured-DPI renderer `0x3D328` |
  | `0x400A86/85/84/87/89/8B/8C/8A/8D` | `0x3D8A8..0x3D922` | Other individual stock fields |

* The counter at `0x400AB6` is decremented at `0x35816..0x3581C`; reaching zero
  invokes the dispatcher at `0x35820`. Battery-related paths set the battery
  flag and reload this counter to decimal 20, for example `0x2ACAE..0x2ACB8`
  and `0x2ADC2..0x2ADCC`. These are concrete background overwrite paths even
  without a button click. No physical milliseconds/refresh-rate claim follows
  from the counter value alone; the full scheduler cadence was not established.
* Dispatcher is also called at `0x351AC`, following resume/connection handling
  which writes the full-redraw flag at `0x351A8..0x351AA`.
* Stock code has direct render calls which bypass that dispatcher: DPI actions
  at `0x325B0` and `0x327C6`, profile-loading continuation `0x30EAA`, settings
  SET handling `0x33DE4`, plus full redraws in startup/mode/profile/wake paths.
  Disabling only the dirty dispatcher would not establish display ownership.

The list is a bounded source/call-site audit, not proof that every indirect
caller or every scheduler interleaving has been enumerated.

## Init, blanking, off and wake are distinct operations

`0x3CDDE` runs a GPIO setup/pulse preamble, transmits 27 initialization command
bytes and a final `AF`. Its observed command stream was:

```text
AE 00 10 40 B0 81 30 A1 C0 A4 A6 A8 1B AD 40 D3 2E
D5 41 D9 04 DA 10 DB 30 8D 52 AF
```

There is no explicit pixel-data transfer or call to the 480-byte clear in this
init body. This is **not** proof that initialization preserves GDDRAM: GPIO
effects and the controller's command/reset semantics remain hardware questions.

`0x3DABC` calls `0x3D824`, which fills pages 0..3 with 120 zero bytes each,
then changes GPIO registers. `0x3DAFA` transmits `AE 8D 10`. Existing power
paths call both in that order, for example `0x34CB4/0x34CB8` and
`0x34D32/0x34D36`; another is `0x2B164/0x2B168`. Thus these paths destroy the
custom pixel contents by explicit software writes, independently of any
retention specification. Their supply-rail/current behaviour was not measured.

`0x34AF8` checks `0x400A7A`. If nonzero, it clears that sleep/resume flag and,
when the screen-hold flag `0x400AF1` is zero, calls init `0x3CDDE` followed by
composite stock redraw `0x3D51E` (`0x34B0E..0x34B18`). With `0x400A7A == 0`,
this init/redraw block is skipped. Subsequent idle-counter refreshes still run.
The GO2 page uploader calls this wake function, but has no saved framebuffer
to reconstruct the last picture after a later stock blank/wake cycle.

## Existing flags are not a verified ownership allocation

* **OLED-off toggle is `0x400AF2`, not `0x400AF0`.** The hold gesture at
  `0x31F26..0x31F4C` toggles it: one branch clears it and initializes the
  display; the other sets it and calls blank/off. `0x400AF0` belongs to another
  screen-setting gesture handled around `0x31F86`; it is not unused owner RAM.
* Setting AF2 does not suppress all drawing. Actual stock instructions for
  both DPI renderer `0x3D328` and raw draw-block `0x3CEE6` issued display bytes
  in offline runs with AF2=1. DPI actions can additionally reinitialize/full-
  redraw while this toggle is set (`0x3259A..0x325B0`). AF2 cannot safely stand
  in for a custom-screen lock.
* AF1 must not be reused: it blocks screen-button event processing and caused
  the original ordinary-key defect.
* `AB6` only delays dispatch; it counts down and is reloaded by stock events.
  `B2F` is existing shared UI state, rearmed to `F0` by multiple paths such as
  `0x330C0`, `0x330D8`, `0x3312E`, `0x34724`. Clearing either does not guard
  direct rendering, blanking or wake. Clearing current dirty flags once is
  likewise insufficient.

No verified reversible ownership policy using these existing bytes was found.
A permanently host-managed display could theoretically suppress stock paths
through code-only changes, but complete coverage, stock-mode restoration and
sleep/wake behaviour would need a separate reviewed design. No such patch was
made. Without retained frame storage, suppressing redraw alone cannot restore
pixels already blanked or lost when the display loses state.

## Offline checks and limits

Unicorn executed original Thumb instructions with ROM/GPIO scheduling and I2C
replaced by explicit stubs; there was no real device access. Narrow button
press/hold/release fixtures kept redraw flags clear for active ordinary
left/right/middle and patched screen-key input. The dirty dispatcher was
separately checked with full/DPI/battery flags and its B2F gate.

Additional observations are saved locally in ignored
`evidence/oled-redraw-observations.json`: init had no pixel-data transfers;
blank/off produced four `78 40` bus preambles plus 480 zero bytes; active wake
did not redraw, sleeping wake did; AF2=1 did not block raw page or stock-DPI
writes. The formatted DPI string was stubbed in the latter test; its numeric
conversion is not a result of this audit. Stubs do not prove electrical state,
real timing, power persistence, complete-loop behaviour or physical display
orientation.
