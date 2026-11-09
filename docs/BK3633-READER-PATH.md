# BK3633 reader-path qualification

Offline audit, 2026-11-09. Target identity is the **replacement actual-unit photographs**: `BK3633QN48`, board `EPC-V18U0002`, dated `2025-4-3`. The first two photos were another mouse and are excluded. This finding does not map `COM1` or any other connector to UART/reset signals. No vendor executable, USB/UART connection, reset or loader entry was run.

The audited artifact remains **BKFIL 2.1.12.1**, `bk_loader.exe` SHA256 `79d1d6e30eda7527099c7bdd2b2241488973b68623b4ecfbe87793a176c0d80b`. It is not silently substituted for the BK3633-recommended 2.1.10.0. Version/source details and the already qualified page-frame codec are in [RECOVERY-UART.md](RECOVERY-UART.md) and [RECOVERY-NEXT-STEPS.md](RECOVERY-NEXT-STEPS.md).

## New bounded proof

| Original PE location | Observation | Consequence for this BK3633 board |
|---|---|---|
| `0x4010AE..0x4010E0`, string `0x4C9938` | `BK3633` is copied into index 3 of a six-name registry. Its consumer `0x4109D0..0x410BB3` opens an input file and matches chip text in its headers. | This literal proves BK3633 **file recognition**, not a selected BK3633 connection class or a safe electrical entry. |
| `0x4AF0E7..0x4AF12E` | Read thread calls connection routine `0x4A8800` **before** dispatching the selected read member. | A clean page-read routine does not establish a clean complete tool session. |
| `0x4A916D..0x4A918E` | SPI preparation `0x4535D0` is gated solely by returned **flash MID `0x1370CD`**, then substitutes `0x1360CD` on success. No MCU/BK3633 exclusion is tested at this gate. | The photographed MCU name does not rule this path out; the unit's actual flash MID is unknown. |
| `0x4536E0..0x45372F` | That preparation issues SPI `0x06` and vendor `0xEF` exchanges, among other transactions. | Its persistent effects remain unqualified; do not run connection/autodetection merely to see whether this vendor-specific path is taken. |
| Read CLI `0x488C08..0x488C28`; connection `0x4A8CBE..0x4A8CD2` | `--bt-nomid` bypasses flash identification by inserting synthetic MID `0x4B42FF`. Table row 71 at `0x4C84F0 + 71*0x44` names that entry **BK3436**, with a 4-Mbit table size. | This is a concrete **wrong-family bypass**, not a safe BK3633 no-prepatch option. Do not use it to invent this mouse's flash identity/capacity. |
| `0x41B7C0..0x41BAAE` | The common read member has no direct unprotect call. The nearby unprotect sequence `0x41BBB4..0x41BBEB` belongs to a separate member beginning `0x41BAB0`. | Narrow positive finding: that read loop is separable from the explicit unprotect member. It does not exclude connection preparation or prove BK3633 selection. |
| `0x41BA1E..0x41BA27`, `0x4AC7C0..0x4AC84F` | Read completion can call a routine emitting `01 E0 FC 02 0E A5` three times when state `+0x10B1C == 1`; that field is the registered reboot option. | Read-session completion can include a reset/reboot command. A reader is not qualified as observation-only merely because erase/program were not selected. No defaults/options are prescribed here. |

The flash table has **no `0x1370CD` entry**; its substituted `0x1360CD` entry is `TH25D_40HB`. Therefore simply suppressing the preparation while retaining the unmodified table-driven vendor workflow is not a demonstrated supported read configuration. Neither this table nor QN48's nominal datasheet capacity identifies the installed flash MID, readable range, encryption/protection or dump format.

## A smaller non-writing primitive exists, conditionally

The independent flash-identification function at `0x405430..0x4055A8` builds one UART-HCI request:

```text
01 E0 FC FF F4 05 00 0E 9F 00 00 00
```

Its SPI portion is the identification opcode `0x9F`; it does not call the vendor preparation, erase, program or unprotect member. An isolated Unicorn check executed these original instructions with serial/CRT calls replaced by local sinks. It captured exactly that one request, accepted a synthetic 15-byte reply and returned the synthetic MID `0x1370CD`. No physical MID was acquired. The existing page function at `0x4142B0` likewise has the previously audited `0x09` page-read framing, independent of the connection wrapper.

This narrows a **future own reader** to explicit identity/page operations after independently qualified transport and entry. Its allowlist could exclude protection changes, vendor repair, erase/program and reset instead of invoking the complete vendor connection/read workflow. It does **not** establish that the current application accepts those commands, that a ROM/loader can be reached safely on this board, that the flash is readable, or that returned data is a complete restorable raw image. The signature/page primitives alone cannot clear the recovery gate.

The inspected CLI registrations do not provide a demonstrated `no-program` / `unprotect-off` / `no-prepatch` combination that turns the complete 2.1.12.1 session into that smaller reader. `--all-chip` selects a generic connection path; `--stay-in-rom` is not an exemption at the MID preparation gate; `--bt-nomid` explicitly uses the BK3436 table entry. No run command or default flag set is recommended.

## Smallest next evidence without invoking a loader

Confirming BK3633QN48 adds no new normal-application USB flash-read dispatcher or host-safe ROM gate to the bounded [normal USB audit](USB-READBACK.md). Do not invent a full dump through that interface.

For this **UART route**, the next hardware evidence is an unpowered board routing check against the correct QN48 reference: identify accessible download/reset/ground connections by an authoritative board schematic or independently checked continuity. Connector proximity or `COM1` text is insufficient. The user reports no USB–UART adapter, so UART acquisition is unavailable now; no adapter purchase, powered probing, guessed wiring, reset or reader launch is proposed. This does not establish that a USB loader cannot read Flash. That distinct protocol is being audited without entering it. Obtaining a qualified reader and proving entry/security/output format remain separate requirements. Firmware installation stays blocked.
