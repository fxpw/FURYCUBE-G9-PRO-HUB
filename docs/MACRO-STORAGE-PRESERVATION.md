# Macro storage during update entry: current bank versus old scratch copy

Updated: 2026-11-09. Offline original-instruction analysis only. No USB/UART
operation, vendor process, loader entry or real flash read/write was performed.
The normalized official 1.08 application is pinned to SHA256
`e0cc9bcb8930ea430d8bc14fefb746f189f0f5f743b81c0b32a5b161f7fba5db`.
It is not a verified dump of the application's installed counterpart.

**Result:** the normal macro getter does not expose a complete raw macro bank.
The stock setter writes 128 bytes per slot, and its scratch copy holds the bank
from **before** the last macro update. Neither a logical macro archive nor the
mere presence of scratch storage clears the recovery requirement.

## Original application routines

| Routine | Observed behavior in the pinned application |
|---|---|
| `0x30FFA` | Reads 108 bytes at `0x7D000 + slot * 128`, then copies those 108 bytes into the active macro buffer at `0x4010AE`. Indices >=32 clamp to zero. |
| `0x30EB0` | Stages the whole old 4 KiB macro bank at `0x52000`, erases the original bank, and restores it with one selected 128-byte slot replaced. |
| `0x39446` | Requests the marker write at `0x7D000` through `0x39CB0`, then resets. |
| `0x39CB0` | Saves a 1 KiB aligned block, requests a 4 KiB erase, patches its RAM copy, and programs only the saved 1 KiB back. |

Lengths are bytes. The low read routine `0x39972` decrements its remaining
length once per `LDRB`/`STRB` byte copy at `0x399CA..0x399D8`. The higher read
wrapper splits accesses at 256-byte boundaries. The erase helper aligns to
4 KiB, and the low erase routine emits controller operation `0x2D`.
Actual installed flash geometry and encrypted/raw representation are still
separate hardware qualifications; the model below assumes flat 4 KiB sectors.

For update entry under that model, the first 16 bytes become the loader marker,
the following 1008 bytes survive, and the last 3072 bytes of the bank are erased.
This supports the existing [entry-loss finding](RECOVERY.md), without turning
the emulated effect into a physical measurement.

## The normal setter stages an old bank, not the current one

The original `0x30EB0..0x30FF8` path performs these operations:

1. Erase `[0x52000,0x53000)`.
2. Copy all four 1 KiB chunks of the old macro bank there.
3. Erase `[0x7D000,0x7E000)`.
4. Restore all four chunks. For the selected chunk, copy the selected **128
   bytes** from shared RAM `0x400DF0` into the chunk before programming it back.

The selected-slot copy is `0x30FBC..0x30FC8`; the 128-byte length is explicit.
There is no corresponding write of that modified chunk back to scratch on this
path. At successful completion, scratch is therefore the previous bank. The
selected slot may differ, including its unexposed final 20 bytes.

Two source fixtures with identical first 108 slot bytes and different final
20 bytes produce identical known getter-visible data but different complete
stored records. The 20 bytes cannot be reconstructed from the 106-byte logical
data plus checksum alone. They are not proven all-FF padding. Their absence
from this getter is also not proof that all boot/stack or other application
paths can ignore them. See the [logical archive limits](MACRO-READBACK.md).

Saving an apparently unchanged logical macro cannot safely manufacture a
current full mirror: the stock setter copies all 128 shared-RAM bytes, while
that getter exposes only 108 stored bytes. A write is not a read-only backup.

## Scratch is shared and durability is not established

Two interrupted-store fixtures stop after an emulated erase. After the scratch
erase, the current bank is intact but scratch is lost. After the macro-bank
erase, scratch contains the old bank and the current bank is erased. No
automatic recovery from these states is demonstrated by these bounded routines.

The inspected related SDK boot project also assigns `0x52000` as
`SEC_PART_BACKUP_OAD_HEADER_FADDR`, with the backup image at `0x52010`.
[Pinned source layout](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_updataImage.h#L17).
Its backup selector can interpret or erase this region. That source has not
been matched to the installed G9 BIM; the overlap is a reason to investigate
scratch lifetime, not a license to rely on it across boot or an OAD operation.

A syntactic scan of the pinned application's Thumb BL candidates found 21
direct references to `0x39BC2`, 14 to `0x39BF0`, nine to `0x39C56`, and two to
`0x39CB0`. The macro getter and setter each have two direct callers. Generic
flash helpers, Bluetooth-storage wrappers and OAD paths accept computed
addresses; there are also external/indirect calls below the app boundary.
This scan is not whole-program proof that no other code can touch either bank.
Startup/default and OAD behavior must not be assumed to preserve scratch merely
because the direct update-entry routine does not write it.

## Reproduction and recovery consequence

[`tools/audit_macro_storage.py`](../tools/audit_macro_storage.py) executes the
original Thumb routines on a locally cached pinned image, with flash APIs
replaced by a synthetic Python bytearray and ROM copy/logging boundaries
stubbed. It performs **38 original-routine invocations**: all 32 slot stores,
two stores for the hidden-byte counterexample, update entry, one getter, and
two erase-interruption cases. It performs no physical I/O and downloads nothing.

```text
wsl --cd D:\repos\fxpw\test\FURYCUBE-G9-PRO -e tools/pyenv/bin/python tools/audit_macro_storage.py
```

All 38 invocations passed their assertions. Optional `--output` writes only an
own synthetic JSON report. Local results and bounded annotated disassembly are
under ignored `evidence/macro-preservation/`; no actual-unit macro data or
manufacturer firmware is published.

Before a destructive app-triggered update entry, a recovery workflow still
needs a qualified copy of all affected current data or a separately proven
equivalent restoration contract. Scratch left by a past macro write and the
logical 32-slot codec do not supply that proof. No new live command or firmware
installation is enabled by this audit.
