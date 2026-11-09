# BK3633QN48: qualified hardware and remaining readback gates

Updated: 2026-11-09. Static research only. No vendor executable, serial port,
mouse command, reset, loader entry, programmer connection or flash operation
was used in this pass. Full backup and independent restoration remain unproven;
the [firmware-installation gate](RECOVERY.md) remains closed.

## Correct target provenance

The user withdrew the first two close-up photographs because they showed
another mouse. They are excluded from target evidence. The replacement five
actual-unit photographs and transcription, reviewed by the parent investigator,
identify **BEKEN BK3633QN48**, board **EPC-V18U0002**, printed **2025-4-3**,
sensor **PAW3311DB-T2MU** and the additional silkscreen **A3311**. Do not carry
forward the earlier tentative `EPC-M8U0002` reading. This researcher inspected
the datasheet pages, not an invented local copy of the user's photographs.
Unit-specific lot codes and photographs are not reproduced here.

The MCU marking resolves the previous BK3533 marketing-name ambiguity for this
unit. It does not reveal the installed ROM/BIM/stack, read protection, encryption
state, board-pad routing or an independent recovery entry. See
[hardware identity](HARDWARE-IDENTITY.md).

## Exact package and capacity evidence

The Beken-authored **BK3633 Datasheet V1.9** hosted by module manufacturer
CionLabs is pinned locally as
`evidence/recovery-uart/BK3633-Datasheet-V1.9.pdf`, SHA256
`aadac18257c97ff31d76547c9f2d9d9b185c7d1ea4aaedf5e22666078960d52d`.
Printed pages 11, 12 and 30 were checked as rendered images as well as text.

Section 7 lists **BK3633QN48: QFN 6 × 6 mm, 48 pins, nominal 512 KB SiP flash**.
It also lists QN49 as 512 KB; suffix increments are not a capacity formula.
Section 2.4 distinguishes QN49's optional pin-14 crystal input from QN48's
GPIO-only P37. Revision history records earlier pin-table changes and a QN49
flash-size correction. Use the exact QN48 section, not an older or adjacent
package diagram. [Beken V1.9, sections 2.3, 2.4 and 7, revision history](https://cionlabs.com/wp-content/uploads/2025/06/BK3633-Datasheet_V1.9.pdf).

The nominal 512 KB is ordering evidence, not a live capacity/access measurement.
It does not establish a complete 524,288-byte dump, dump representation or
restorability. No capacity default was added to the offline archive tool.

## Chip-label reference, not board-pad instructions

The following labels combine the exact package table with the programming-mode
function table; they do not identify any accessible point on EPC-V18U0002.

| QN48 package pin | Datasheet label | Relevant reference function |
|---|---|---|
| 13 | RSTN | Active-low chip reset |
| 38 | P00_DN | Programming UART TX; also USB DN |
| 39 | DP | USB DP, distinct from UART RX |
| 40 | P01 | Programming UART RX |

[Beken V1.9, printed pages 11–12 and 17](https://cionlabs.com/wp-content/uploads/2025/06/BK3633-Datasheet_V1.9.pdf).
Tuya's **exact BK3633QN48** production table independently identifies RX=P01,
TX=P00_DN and RST=RSTN. Its example is a production interface, not this mouse's
schematic or a qualified backup procedure.
[Tuya chip-production reference](https://developer.tuya.com/en/docs/iot/chip-burn-and-authorization?id=Kceuttbn7mdc4).

Consequently, assuming USB D+ is UART RX would select the wrong QN48 signal.
A USB connector, a `COM1` board reference or visually nearby pads cannot supply
the missing routing proof. Neither a generic development-board supply example
nor the package's power-pin labels establish this board's UART logic voltage.

## Exact reader status

Tuya recommends **BKFIL 2.1.10.0**, without online updating, in the bundled
`vendor\bk3633_ble\sdk\BK3633_DesignKit_V06_2411\Tools` directory. Its guide
shows a distinct Read Flash action. This pass found the recommendation and
bundle location, but **did not obtain a primary-source, hash-pinned 2.1.10.0
binary**. Its connection/read path therefore has not been audited. A screenshot
of a Read button cannot establish an end-to-end non-mutating reader.
[Tuya BK3633 platform guide](https://developer.tuya.com/en/docs/iot-device-dev/bluetooth_platform_ble_bk3633?id=Kdi0in7x1sbwe).

The available **2.1.12.1** is a separate artifact, not a qualified substitute:
`bk_loader.exe` SHA256
`79d1d6e30eda7527099c7bdd2b2241488973b68623b4ecfbe87793a176c0d80b`.
Previous static inspection found the real 4 KiB read-page routine at `0x4142B0`
and whole-flash loop at `0x418AE0`. Its connection precedes the read: at
`0x4A916D` flash MID `0x1370CD` can trigger `0x4535D0`, which includes SPI
write-enable/vendor-command transactions. No BK3633 exemption was established.
The newer tool's `BK3633` string alone does not select a proven safe connection
class. These are reasons to finish an exact selected-path audit, not proof that
every connection writes or that 2.1.10.0 behaves identically. See the existing
[read-loop/connection analysis](RECOVERY-NEXT-STEPS.md#confirmed-offline-uart-page-contract-and-its-limitations).

The own [offline archive validator](../tools/g9_backup_archive.py) opens only
already captured local files; it cannot acquire flash or establish electrical
access. Its existing synthetic fixture is not mouse data.

## Independent entry and security

V1.9 describes programming-mode selection at startup, but the reviewed material
does not establish this board's selection waveform or an entry independent of
a working main application. Any dependency on mutable BIM must be identified;
an immutable ROM path must not be inferred from a symbol filename. Section 3.9 permits permanent download/
debug disabling and irreversible NVM access restrictions, with per-unit code
encryption. The actual unit's policy is unknown.
[Beken V1.9, sections 3.2 and 3.9](https://cionlabs.com/wp-content/uploads/2025/06/BK3633-Datasheet_V1.9.pdf).

Do not treat ordinary mouse USB update entry as a harmless alternative: the
audited application path touches the macro-storage sector before reset.
Neither changing security nor installing a flash-reading patch can provide the
required pre-installation backup. Current boundaries are documented in
[USB readback](USB-READBACK.md) and [UART recovery](RECOVERY-UART.md).

## Minimum dependencies for the UART route

The user reports **no USB–UART adapter**. Multimeter availability/model remains
unconfirmed. The photographs still showed a connected battery; switch state
and absence of electrical power were not established. There is no qualified
electrical experiment to perform now, and no adapter purchase recommendation
follows from these findings. This equipment limitation applies to the UART
route; it is not proof that flash readback over a correctly qualified USB
loader is impossible. USB-loader command/discovery analysis continues separately.

| Missing proof | Minimum useful next evidence | Limit |
|---|---|---|
| Exact non-mutating reader | Original BKFIL 2.1.10.0 package and configuration from the documented SDK, retained with hashes; offline call-path audit | No launching or connecting to see what it does |
| App-independent entry | Exact installed ROM/BIM/loader entry and read-policy evidence applicable to the unit | SDK boot code does not prove the installed boot path; app-triggered update does not prove entry with a failed app |
| Accessible board routing | If not already visible, one sharp reverse-side/connector-region photograph retaining chip orientation; then a board schematic or continuity map | No further chip-marking/FCC photo is needed; a photograph alone cannot trace hidden vias |
| Electrical mapping | Confirm suitable meter and fully isolated power before any separately reviewed continuity measurements of accessible pads | No probing the present battery-connected board or guessing direct QFN contacts |
| Logic levels and contention | Board-specific voltage/topology evidence for the mapped UART/reset nets | USB and battery voltage do not establish UART input tolerance |
| Read representation and completeness | After all preceding gates, two independently acquired matching full-capacity passes, plus explicit handling of NVM/protected data and restore format | A matching app resource or settings archive is not a full backup |

No pad number, wire assignment, reset timing, baud-rate command, security flag
or live reader invocation is prescribed here. The current actionable work is
offline reader qualification and board-access documentation. Any acquisition
requires a later reviewed procedure; installation still requires independently
reachable, verified restoration.
