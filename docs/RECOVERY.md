# Recovery status and firmware flashing gate

Updated: 2026-11-16. **NoWait, OL3 and OL4 have been installed in owner-authorized
experiments on the development mouse. Independent recovery remains unverified.**

**Current post-reboot validation:** the installed 708-byte EP2-guard OL4 passed
input checks after a single short OLED command, a complete initialized frame,
opening the HUB, a timed ten-second live stream, and stopping the stream. The
owner confirmed simultaneous working input and updating widgets. Passive G9
capture recorded 843 movements and two left clicks during the live interval;
its right-button counter did not increase, so that interval's right-click result
is user-confirmed only. Mouse input and the assigned key also worked after stop.

Windows reboot was required to recover input from the earlier failed session.
The full persistent cause has not been isolated; the current guard's model
passed 53 FIFO/readiness cases, and the limited physical results above do not
establish general recovery or long-duration reliability. No further flash was
performed after reboot. The owner also confirmed the reduced PNG at 84×20,
position (18,4), with cursor and clicks still working. The final 2.4 GHz check
passed: user confirmation and passive G9 input cover movement, left/right clicks
and the assigned key. After changing from cable to radio, the owner saw the stock
screen instead of the PNG. The exact reset trigger was not isolated; cross-mode
image retention is unsupported. Source publication and final CI have not completed.

The OEM host completed 33/33 blocks for each reviewed image and the normal wired
application returned. NoWait's ordinary OLED-button key output was confirmed over
USB and 2.4 GHz with the HUB closed. OL3's expected version-report marker was
read from the physical mouse. Its first image transfer failed and the configuration
interface stopped responding; power-cycling restored the OL3 response. A physical
GS3 numeric-DPI status reply then passed validation. The long-write failure was
reproduced on the original USB OUT assembler: the final fragment of a 132-byte
transfer is parsed as its header. The native client now blocks OL2/OL3 image
writes. The reviewed OL4 replacement completed 33/33 blocks and returned to the
normal application; its OL4 marker and CRC-valid 64-byte GS4 status were read.
An initial frame attempt stopped at discovery validation before any image command.
After power-cycling, all twelve short test-frame reports were submitted
successfully. The owner confirmed the visible border and readable “OLED OK” /
“G9 PRO” text. This is a first-image result, not a sleep/retention or live-widget
test on the initial OL4 image. The later OL4 with the discovery-race correction
also completed 33/33 blocks and returned to the normal application. Four
consecutive physical status/version cycles passed, each with a valid GS4 CRC
and the correct following version header and marker. The owner then confirmed
that the clock, battery, CPU and RAM widgets were visible and updating on this
corrected image while the HUB ran. Sleep/retention remains outside these tests.
Normal cursor/click input subsequently failed, including after closing the HUB
and helper, and recovered only after a Windows reboot. The current EP2-guard
image and successful staged post-OLED input retest are described above. See the
[USB packet-boundary investigation](OLED-USB-CHUNKS.md). Host completion and an
application response are not full-flash readback or recovery validation.

## Configuration restoration after the experiments

Supported original settings were restored with strict readback: the six enabled
DPI stages are 1600, Angle Snap is enabled, and the profile count is 1. The button
table matches the saved original table with the chosen OLED-key assignment
retained. The HUB remains necessary for live PC widgets, not ordinary button output.

This is **not a bit-exact restoration of all 117 profile bytes**. The only remaining
named-field differences are inactive DPI slots 7/8: their original value was 0,
while the factory-initialized value is 100. The guarded setter's minimum is 50,
so it refused 0; these slots are not enabled. Opaque reserved and lighting bytes
were not restored byte-for-byte. No complete installed-firmware backup or
independent recovery method was established by this settings restoration.

## Current owner authorization

On 2026-11-15 the owner explicitly accepted possible permanent loss of this
mouse and authorized an experimental replacement, including firmware written
from scratch if that makes the task easier. This supersedes the earlier
owner-specific absolute requirement to prove recovery before any installation.
Do not ask again for the same accepted risk or retain the old requirement as
an independent reason to block the approved experiment.

Recovery is still **unverified**, and the risk acceptance is not proof of a
backup, compatibility or restoration. Prefer the smallest applicable change
and preserve available settings/data. The concrete image, target board and
installation path still require review. A full rewrite is an option to compare,
not evidence that it is simpler than the existing narrow 1.08 patch.

The standard public release gate continues to require independent recovery of
every affected region. A complete current dump is one route; an equivalent
verified app-only rollback can suffice if BIM/stack and all other required state
are preserved or restorable. A private owner-authorized experiment is not a
general safe-install claim for all users.

The GUI, input diagnosis, offline emulation, candidate preparation and source
publication can continue. Existing candidate files have `.exe.disabled` names;
their names alone do not identify the currently reviewed image. Do not launch
an arbitrary historical candidate as a recovery test. Installation preparation
must select and verify the exact intended candidate and path.

An offline [minimal-sector plan](MINIMAL-UPDATE-PLAN.md) now shows the exact
smaller application-write scope for a future preserving implementation. It is
not a transport or installer and does not change the OEM updater's broad erase
range or its entry/finish side effects.

**Do not enter the OEM USB loader as a harmless probe.** Its application entry
routine writes the same logical flash base used by onboard macros. The
read/erase/write analysis below adds a configuration-loss risk before an app
update has even started.

**The replacement target-unit photographs identify BK3633QN48**, on board
`EPC-V18U0002`, printed `2025-4-3`. The user withdrew the earlier reference
pictures as belonging to another mouse. This now qualifies BK3633 package
research; it does not establish board pad routing, security state, reader
compatibility or independent recovery. See [hardware identity](HARDWARE-IDENTITY.md).

## What is actually established

| Item | Evidence | Current limit |
|---|---|---|
| Software request to enter the updater | Official application handler `0x39446`, labelled `app_usb_upgrade_start`, writes a 16-byte marker and resets the CPU | Requires the existing application to execute; not an independent recovery path |
| Marker used by the application | Source bytes at CPU address `0x447EC`: `12 34 AA 00 00 20 00 00 00 00 00 00 00 00 00 00`; handler targets flash address `0x7D000` through `0x39CB0`, then calls reset `0x3B618` | Bootloader handling/clearing of this marker is not contained in the downloaded app resource and has not been verified |
| USB loader used by the OEM updater | Static analysis identifies application VID/PID `1D57:2142` or `1D57:2148`, entry feature `0x10` and loader `A745:0033`; the reviewed NoWait/OL3/OL4 installations physically completed through that loader and returned to `1D57:2148` | Entry was from a working application; this does not prove recovery with an unbootable app or verify every programmed byte |
| OEM erase/write behavior | Analyzed updater erases `[0x2B000,0x51000)` and writes `[0x2B00A,0x4C00A)` for the original transfer size | Erase covers more than the supplied app data; the updater does not first save the existing contents |
| UART programming at chip level | Beken documents a UART download interface for BK3633 | Mouse PCB access, board revision, usable pads, security configuration and read/restore support remain unknown |
| Current device firmware backup | None | Downloading the official app image or reading mouse settings is not a complete backup |
| Hardware/button recovery with an unbootable app | Not established | No verified G9 PRO button combination, boot strap or app-independent USB entry is available |

The marker write above was first established by offline disassembly. The later
owner-authorized installations used the OEM update path, including its entry
and erase/write behavior. The marker's physical bytes and macro-bank effects
were not read back independently; no UART flash dump or recovery test was made.

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

## Entering the loader can modify macro storage

Offline analysis of the pinned official app found both of these paths:

| Path | Arguments established from original instructions |
|---|---|
| Onboard macro read, `0x30FFA` | Calls `0x39BC2` at `0x7D000 + index * 0x80`, length 108, with index clamped to 0–31 |
| Software update entry, `0x39446` | Calls `0x39CB0` with the marker at `0x447EC`, destination `0x7D000`, length 16 |

These are the same logical flash address space: `0x39CB0` uses the same read
backend to save 1024 bytes, calls its erase helper with length 4096, patches the
16 marker bytes and writes back 1024 bytes. The erase path issues controller
command `0x2D07D000`; the related SDK identifies the sector-erase opcode as a
4 KiB operation. Under that sector geometry, the marker replaces the start of
zero-based macro index 0 (host slot 1), and the remaining 3 KiB of the erased sector are not restored by
this routine. The SDK/controller-identity limitation above still applies.

This is an **offline code finding**, not a destructive test on the user's mouse.
The physical erase effect has not been measured. Loader entry cannot be called
a harmless observation. Under the owner's new accepted-risk experiment it may
be part of a reviewed installation, with this known configuration-loss risk
recorded and available settings saved before entry, not only before main erase.
The ordinary-USB readback audit is in [USB-READBACK.md](USB-READBACK.md).

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

The separate [UART reader/security audit](RECOVERY-UART.md) identifies concrete
tools, the full-range read limitation, and permanent download/debug protection
documented for the researched SDK. Its prerequisites remain unresolved on this mouse.

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

Some board continuity has now been measured: USB D− to chip pin 38 and the middle
left-click switch contact to pin 40/P01. This is not a qualified programming
pinout. GPIO levels, power/reset sequencing, security state and any mode-selection
strap remain unresolved. See [board continuity](PCB-CONTINUITY.md). No programming
wiring or random button combination is prescribed by this document.

## Preserved partial baseline

A separate local copy of the last validated 59-byte button table and the pinned
original updater is kept outside the source checkout under
`%LOCALAPPDATA%\FURYCUBE-G9-PRO\recovery`. The table is also available in the
ordinary `backups` directory for the HUB's button-table restore dialog. The
copied updater keeps an `.exe.disabled` extension and is only a reference.

The baseline was derived from an existing validated device-read capture; saving
it issued no new device command. Checks cover its header, checksum, preserved
left click and exact byte roundtrip. Its manifest explicitly says **not a full
installed firmware backup**. It contains no bootloader, stack, complete macro
bank, pairing/calibration state or protected NVM. A successful button-table
restore would not prove complete device recovery. These local files are not
published.

## Evidence needed for a verified recovery claim and public release

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

Until this evidence exists, recovery and general safe installation must not be
claimed. The owner-specific accepted-risk experiment above is a separate
authorization; it does not supply any of this missing evidence.

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

Recovery research has a plausible chip-level direction, but complete safe
restoration has not been demonstrated. The owner has now authorized a reviewed
experiment despite that uncertainty; the public recovery claim remains unproven.
