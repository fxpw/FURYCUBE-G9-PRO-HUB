# Standalone FT232RL check

2026-11-15. The adapter was connected only to the PC, with no mouse wiring.
The user reports having received the remaining measurement/wiring supplies.

The selected adapter is a USB-C FT232RL module with a 3.3 V / 5 V jumper.
The user confirmed the jumper joins the middle contact to the 3.3 V side.
Windows identified the adapter as FTDI `0403:6001`, with a working virtual COM
port. Local COM numbering is not a device identity or a fixed requirement.

## Measurements and loopback

- The user measured **3.29 V** between TXD and GND using the multimeter while
  the port was held idle. This is a user-reported voltage, not a captured meter
  trace or an independent calibration of RX thresholds.
- With USB unplugged, the user was instructed to connect TXD to RXD and then
  reconnect the isolated adapter; the user confirmed the setup was ready.
- The test freshly selected exactly one ready `FTDIBUS` port with hardware ID
  `FTDIBUS\COMPORT&VID_0403&PID_6001`; it did not guess the port from its number.
- Settings were **115200 baud, 8 data bits, no parity, one stop bit**, no hardware
  handshake, DTR/RTS disabled and BREAK inactive.
- A fresh random ASCII test string was sent once: **44 bytes sent, 44 received,
  exact byte-for-byte equality**. The port was closed and disposed afterward.

The private result is retained under ignored `evidence/ftdi-bench/` as
`20261115T1548080866077Z-loopback.json`. No mouse command or firmware operation
was performed, and no manufacturer program was launched.

This qualifies the adapter's tested TX voltage and standalone data loop at
that baud rate. It does not establish the mouse PCB routing, required logic
voltage, reset/boot entry, flash representation, security policy or restoration.
The next step is inspection and measurements on the fully unpowered mouse
board before any adapter-to-mouse connection. The [recovery gate](RECOVERY.md)
remains in effect.

## Actual module photographs and pending VCCIO mapping

The user subsequently supplied front/back photographs of the received module.
The visible PCB markings are `YP-05` and `V1350`; it has USB-C, a three-pin
5V/3.3V selector, and a side hole marked `VCC` between `RXD` and `RTS`.
These are observations of this module, not a manufacturer's circuit diagram.

The user then measured the mouse's released P01/left-switch-middle terminal
at **2.77 V**, separately from the adapter's earlier **3.29 V** idle TX.
Direct electrical compatibility is not established by those readings.

FT232RL's VCCIO is package pin 4, a supply for the UART/CBUS I/O domain;
[FTDI's datasheet](https://ftdichip.com/wp-content/uploads/2020/08/DS_FT232R.pdf)
documents an external I/O supply range of 1.8–5.25 V. That chip capability
does not establish the selector or side-hole routing on this actual module.
Do not treat the mouse's P01 input/pull-up as a supply source.

With adapter USB and wires disconnected and the selector jumper removed, the
user reported **1.2** between its middle contact and the side hole labelled
VCC. A subsequent check to the FT232RL's right-side fourth terminal from the
bottom (USB connector upward, package pin 4/VCCIO) was compared against shorted
probes: the user reported **0.05** for the short and **0.08** for the pin check.
The instructed range was 200 ohms; these last values are retained as reported
display strings, not calibrated ohm measurements. A later actual-meter photo
confirms the 200-ohm selector position, not the earlier numeric transcriptions.
The paired reports support a near-baseline connection.

The user subsequently reported overrange between the middle selector contact
and both fixed side supplies (3.3V and 5V), but the module LED lit during the
5V-to-middle test. The user subsequently reported that the LED lights with one
probe polarity only, while the display remains overrange in both directions.
Which specific probe was on 5V was not reported. The actual-meter photo shows
the FAZA M832 set to 200 ohms, red lead in VΩmA and black lead in COM. USB
disconnected and selector cap removed remain the requested setup, not
independently visible in that meter photo.

A resistance/diode tester can supply current through semiconductor paths;
an LED lighting is not, on its own, proof of a hard short or of a faulty module.
Conversely, overrange on a low resistance range does not establish electrical
isolation. Do not apply an external VCCIO supply or connect the mouse on the
basis of these results. Fixed-rail isolation/backfeed behaviour and any
external-supply arrangement still need qualification before an adapter-to-mouse
connection.

### Supply-source constraint and next candidate

FTDI's actual SSOP pin-4 description requires a lower external VCCIO supply
to originate from the **same source as core VCC**: for a USB-bus-powered device,
use a regulator supplied by USB 5V. See printed page 7 and section 6.4 of
[FT232R datasheet V2.15](https://ftdichip.cn/Support/Documents/DataSheets/ICs/DS_FT232R.pdf).
The chip's voltage range alone does not qualify feeding VCCIO from the mouse
battery/regulator. Do not do that.

A candidate requiring no cross-connection of the two power rails is a TX-only
resistor divider, with the FTDI selector restored to its known 3.3V position
while USB is unplugged. Candidate values for an isolated bench test are
1kΩ above / 4.7kΩ below, or 2.2kΩ above / 10kΩ below, yielding approximately
2.7V from the reported unloaded 3.29V TX. Component availability, actual
divider output under adapter drive, tolerance/loading and power/connection
sequencing must be established before connecting it to the mouse.
This is not yet a mouse-wiring or download instruction.
