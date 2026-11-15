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
