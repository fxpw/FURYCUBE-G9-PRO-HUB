# Recovery status and firmware flashing gate

Updated: 2026-11-09. **No firmware has been installed by this project.**

The user requires that the device can be restored completely. This is a hard
requirement: firmware installation stays blocked until recovery independent of
a working application is confirmed and the current device contents are backed
up, or an equivalent manufacturer-supported recovery procedure is proven for
this exact hardware. A request to accept the risk does not replace this gate.

The GUI, input diagnosis, offline emulation, candidate preparation and source
publication can continue while flashing remains blocked. Candidate files have
`.exe.disabled` names and must not be launched as a recovery experiment.

## What is actually established

| Item | Evidence | Current limit |
|---|---|---|
| Software request to enter the updater | Official application handler `0x39446`, labelled `app_usb_upgrade_start`, writes a 16-byte marker and resets the CPU | Requires the existing application to execute; not an independent recovery path |
| Marker used by the application | Source bytes at CPU address `0x447EC`: `12 34 AA 00 00 20 00 00 00 00 00 00 00 00 00 00`; handler targets flash address `0x7D000` through `0x39CB0`, then calls reset `0x3B618` | Bootloader handling/clearing of this marker is not contained in the downloaded app resource and has not been verified |
| USB loader expected by the OEM updater | Static updater analysis recognizes application VID/PID `1D57:2142` or `1D57:2148`, requests update with feature `0x10`, then opens loader `A745:0033` | No physical loader transition was performed for this audit; enumeration alone would not prove recovery |
| OEM erase/write behavior | Analyzed updater erases `[0x2B000,0x51000)` and writes `[0x2B00A,0x4C00A)` for the original transfer size | Erase covers more than the supplied app data; the updater does not first save the existing contents |
| UART programming at chip level | Beken documents a UART download interface for BK3633 | Mouse PCB access, board revision, usable pads, security configuration and read/restore support remain unknown |
| Current device firmware backup | None | Downloading the official app image or reading mouse settings is not a complete backup |
| Hardware/button recovery with an unbootable app | Not established | No verified G9 PRO button combination, boot strap or app-independent USB entry is available |

The marker write above is a description of disassembled code, **not an action
performed during the audit**. No USB command, mode change, reset, flash read,
erase or write was sent to the mouse for recovery research.

## Why the official updater is not a rollback guarantee

The audited `Update.exe` code (`0x402F50…0x403CBB`) initiates the loader, erases
and programs the supplied image. No read-flash backup operation was identified
in that path. After it opens the loader, it proceeds into erase/programming;
opening it to see whether recovery works is therefore not a harmless probe.

The downloaded resource contains an application update, not a verified full
dump of the currently installed mouse. It does not establish the exact board
revision, bootloader, protocol stack, device-specific nonvolatile data,
calibration, pairing information or security state. Reinstalling that resource
cannot be described as restoring all previous contents.

The original official updater is retained byte-for-byte and its SHA256 is:

```text
558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4
```

Keeping it is useful for analysis, but it is not a replacement for an installed
firmware backup and an independent way to enter the programming interface.
Matching PID, firmware version `1.08`, valid CRC blocks, the OL2 marker, or a
successful offline emulator run does not prove recovery or boot compatibility.

## Application startup and button combinations

The string `key press before cpu reset` at `0x2A234` was checked in context. Its
reference at `0x29EBC` belongs to RF-mode/pairing initialization around
`0x29E72…0x29EC4`. It is not evidence of a hardware bootloader key combination.

The OLED button's hold gesture and the known right-button plus OLED-button
behavior execute inside the running application. They must not be advertised
as recovery controls when that application cannot start.

No manufacturer instruction confirming app-independent G9 PRO USB recovery was
found in the official firmware archive or support material reviewed so far.
This is a bounded negative finding, not proof that such a mode cannot exist.

## A plausible hardware route, not yet a mouse recovery procedure

Beken's BK3633 product page explicitly lists UART download support, SiP flash
and security features including code encryption and secure JTAG. Those features
make UART investigation reasonable but prevent assuming that every device can
be freely read and rewritten. [Beken BK3633 product documentation](https://www.bekencorp.com/en/goods/detail/cid/43.html).

Beken's development framework documents UART programming with its BKFIL tool
on a BK3633 development board. This demonstrates a platform facility, not the
G9 PRO's pad routing or factory boot configuration.
[BK3633 development-board programming guide](https://docs.riselink.ai/arminodoc/bk_findmy/bk3633/en/v2.0.1/get-started/index.html).

The BK3633 platform guide distributed by Tuya also distinguishes full
boot+stack+app images from app-only updates and describes UART programming and
flash-reading tools. It is a useful source for future read-only investigation;
it does not validate any recovery action on this mouse.
[Tuya BK3633 platform guide](https://images.tuyacn.com/goat/pdf/1718099239374/BLE-BK3633_TuyaOS_TuyaOS.pdf).

No PCB pinout has been verified. Chip GPIO signal names in a generic datasheet
are not enough to identify safe connection points, voltage levels, power/reset
sequencing or a mode-selection strap on the mouse. No wiring or random button
combination is prescribed by this document.

## Evidence needed before the flashing gate can be cleared

1. Establish the exact board/chip revision and an entry method that still works
   when application code is invalid. Obtain a manufacturer-confirmed method,
   or validate a hardware loader/ROM interface without erasing anything.
2. Establish a genuinely read-only tool/protocol for that interface. A button
   labelled “Read Flash”, a guessed loader opcode, or an unrelated BK SDK is
   insufficient until its operations and supported device/security mode are
   understood. The current OEM updater is not that tool.
3. Read the current device contents, at least every region any update can
   erase/write plus device-specific configuration needed for full restoration.
   Prefer a complete accessible flash dump, read twice and compared by hash;
   record identity, sizes, addressing/CRC/encryption format and protected areas.
4. Demonstrate a compatible restore path from the saved representation without
   depending on a working patched app. Avoid destructive validation on the only
   mouse; a matching spare board or a documented manufacturer procedure may be
   needed to establish this safely.
5. Keep the actual backup and recovery record local. Do not publish unique
   pairing/calibration information or proprietary complete firmware images in
   the public source repository.

Until this evidence exists, preparing a reviewed candidate is allowed, but
running an updater or writing the candidate to the device is not.

## Preliminary time estimate

These are planning estimates, not measured work time or a recovery guarantee:

- A focused read-only pass over the available firmware, updater and official
  programming documentation: approximately **20–40 minutes**. This can identify
  supported mechanisms and missing prerequisites; it cannot invent a verified
  hardware recovery route.
- Once a documented read/entry channel and the necessary equipment are
  available, preparing and verifying a dump/restore workflow may require a
  separate **1–2 hour** session. This estimate is conditional on that channel
  actually supporting the installed device and its security configuration.
- If opening the mouse, identifying pads, obtaining a programmer, resolving
  protected flash or manufacturer assistance is required, there is **no honest
  fixed completion estimate yet**. Equipment availability and the board design
  become the limiting factors.

The current result is therefore: recovery research has a plausible chip-level
direction, but complete safe restoration has not been demonstrated. Firmware
installation remains blocked; HUB development does not need to stop.
