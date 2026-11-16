# G9 PRO board checks: 2026-11-15

Target: the user's actual **BK3633QN48 / EPC-V18U0002 / 2025-4-3** board.
The user withdrew the earlier photographs of another mouse. They are not
evidence for these measurements.

## Reported measurements

The following are user-reported multimeter readings. Probe placement was not
independently captured. Resistance measurements were requested with the mouse
USB, battery and FT232RL disconnected, on the meter's 200-ohm range.

| Measurement | Reported result | Interpretation |
|---|---|---|
| Shorted meter probes | Approximately 1 ohm; previously 1.2–2 ohms | Lead/contact baseline, not a calibrated zero |
| Rear D− pad to package pin 38 | 1.6 ohms | Consistent with a direct connection |
| Worn rear pad to package pin 43 | Approximately 1–1.2 ohms | Consistent with P04 |
| Same worn pad to package pin 40 | Single `1` at the left of the display | Overrange on 200 ohms; no near-baseline connection found |
| Rear pad labelled VPP to pin 40 | Overrange | Does not identify a P01 breakout |
| Rear pad labelled P16 to pin 40 | Overrange | Does not identify a P01 breakout |
| Left-click switch middle solder terminal to pin 40 | Contact with switch released and pressed; other two terminals overrange in the initial released test | User measurements support an accessible P01 connection at the middle terminal |
| P16 to GND, battery-powered in 2.4G mode | 0 V | One idle DC reading; does not prove absence of activity |
| Rear VCC to GND in the same powered test | 2.7 V | Voltage at that pad; its supply domain is not established |
| Left-click switch middle terminal (P01 candidate) to GND, released, battery-powered in 2.4G mode | 2.77 V | Reported idle input voltage; not an absolute-maximum rating or power-source qualification |

In the chat, **contact** means a reading near the shorted-probe baseline and
**open** means the single left-hand `1`. Here, “open” is shorthand for exceeding
the selected range, not proof of infinite resistance. An actual numeric zero
is a low resistance reading and must not be confused with overrange.

After the voltage checks, the user was instructed to switch off and disconnect
the battery. No subsequent powered state or adapter connection is assumed.

## What the firmware establishes

Static inspection used the original normalized 1.08 application, SHA256
`e0cc9bcb8930ea430d8bc14fefb746f189f0f5f743b81c0b32a5b161f7fba5db`.
These facts describe the inspected application, not the missing installed
bootloader or physical PCB routing:

- UART2 initialization at `0x3BBDC` configures P16/P17, but no direct caller or
  ordinary function-pointer reference was found in the inspected application.
- Formatted output at `0x3BC66` is the stub `movs r0,0; bx lr`, despite 589
  call sites and retained startup/reset/OLED message strings. The buffer-dump
  wrapper at `0x374A8` is also a return stub.
- Therefore P16 has no established normal startup-log prediction. The earlier
  suggestion to look there for a known boot log is superseded. Unknown code
  outside this application could behave differently; silence cannot rule out
  a programming interface.
- The UART0 115200 initialization reached through `0x28C4E` belongs to the
  `enter_dut_fcc_mode` branch, not an established flash-reading handshake.
- OLED data/clock writes at `0x3CD7A` address P20/P21, and its GPIO setup at
  `0x3CD40` also writes P22/P23. This does not justify assigning P01 to the OLED
  connector.
- GPIO initialization at `0x3164A..0x31650` configures P01 as an input; the
  matrix input routine at `0x2AAD8` reads P01 as column bit 0. A switch contact
  is therefore a useful place to investigate for an accessible P01 connection,
  subject to a separate unpowered continuity check.

The first matrix row selects P24; P01 in that row produces raw bit 0, corresponding
to the left-button slot (index 0 in the binding catalogue). This makes the
physical left-click microswitch's solder terminals a reasoned **test candidate**,
not an established UART pad. With all power isolated and the switch released,
check package pin 40 against each of its three solder terminals separately.
Do not assign COM/NO/NC or a particular terminal from the photograph. A
near-baseline result is needed before declaring an accessible P01 connection.
The user subsequently reported contact on the middle terminal only, then
confirmed that middle-terminal-to-pin-40 continuity does not change when the
switch is clicked. This supports using that terminal as an accessible P01
connection, conditional on the reported probe placement. The subsequent user
measurement at the released terminal was 2.77 V with battery-only power in
2.4G mode. UART electrical compatibility remains to be established; the input
terminal must not be treated as a power supply for an adapter.

## Remaining gates

The worn rear pad is supported as **P04**, not programming RX. User measurements
support the left-switch middle terminal as a P01/pin-40 connection. Reset routing,
actual GPIO voltage domain, compatible
read-only entry, full dump representation and independent restoration remain
unqualified. The measured 2.7 V must not be treated as proof that FT232RL's
previously measured 3.29 V TX is acceptable on P01.

No mouse UART command, boot-mode transition, protection change or firmware
write was performed in these checks. Do not use a test-pad label, retained
debug string, or generic SDK initializer as permission to guess wiring or
loader commands. See [recovery requirements](RECOVERY.md) and
[QN48 reference limits](BK3633QN48-RECOVERY.md).
