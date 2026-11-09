# UART recovery research: tools, security and missing prerequisites

Updated: 2026-11-09. This is a **read-only research result**, not a wiring or
recovery procedure. No vendor program was executed, and no USB/UART command,
reset, mode change, erase or programming operation was performed. Firmware
installation remains blocked by the [recovery requirement](RECOVERY.md).

## Hardware identity comes first

The G9 PRO manufacturer's product material names **BK3533**, while the SDK
examined here is for **BK3633**. Similar firmware organization and peripheral
code do not identify a physical chip or its security configuration. The
normalized G9 application has no explicit printable ASCII `BK3633` or `BK3533`
identifier. These BK3633 findings therefore remain conditional until the exact
chip/board revision is established. See [hardware identity](HARDWARE-IDENTITY.md).

Do not use a BK3633 package pin number, boot command, flash capacity or security
assumption as a verified G9 PRO fact.

## A concrete reader exists, but is not yet an approved G9 reader

Tuya's BK3633 platform guide names **BKFIL v2.1.10.0**, distinguishes its flash
reading action from erasing/downloading, and recommends that bundled version
without online updates. It documents programming UART0 at P00/P01 plus reset
control. It also distinguishes boot+stack+app production files from app-only
updates and stack+app updates. None of this establishes the mouse's pad routing
or guarantees an encrypted full-chip backup.
[Tuya's BK3633 platform guide](https://developer.tuya.com/en/docs/iot-device-dev/bluetooth_platform_ble_bk3633?id=Kdi0in7x1sbwe).

Beken's general BKFIL guide documents the separate `bk_loader.exe read`
subcommand and explicit hexadecimal address/length selection, for example:

```text
bk_loader read -p 7 -i all.bin -s 0-1000
```

In that example the length is 0x1000, **not the complete flash**. Its `-f`
syntax can specify named files and multiple ranges. These examples appear in
the general/Wi-Fi-family guide: they establish tool functionality, not BK3533
or G9 compatibility, the proper port, boot-entry protocol or full-dump format.
The default read length can follow the selected input file's length, so simply
selecting the OEM app image would not establish a whole-chip backup.
[Beken BKFIL command-line documentation](https://docs.riselink.ai/arminodoc/bk_idk/bk7258/en/v_ai_2.0.1/developer-guide/config_tools/bk_tool_bkfil.html).

The publicly available manufacturer SDK contains a serial writer **1.54** and
a USB download tool **2.5.5**, not the recommended BKFIL 2.1.10.0. The archives
were downloaded and listed/extracted without executing them. Their presence
does not establish a safe read path.
[Beken's SDK download-tools directory](https://github.com/bekencorp/bk3633_mesh_sdk/tree/70d28da4d0c97d45a119644d9eb9abd0db459b14/download_tools).

A separate primary distribution, Quectel's **BKFIL 2.1.12.1**, was downloaded
for static inspection only. Its `bk_loader.exe` includes BK3633 identification,
a read subcommand and range-reading options. This version is **not substituted
for the BK3633-recommended version**. It also contains protection-changing and
key-programming code; strings alone cannot prove those routines are excluded
from every connection/read path. The appropriate chip-specific call path must
be audited before calling its read operation non-mutating.
[Quectel's BKFIL distribution page](https://developer.quectel.com/doc/shortrange/FGM842D/en/Resources/Tools/bkfil.html).

## What ROM entry and security documentation actually establish

The BK3633 datasheet supplied by Beken's public SDK describes startup mode
selection and download UART signals. It does **not** specify an adequate
mode-selection waveform, handshake sequence or a G9 button combination.
Consequently, a way to reach a reader with an invalid application is still
unproven here; neither the SDK app boot source nor an application-triggered
USB update request proves immutable-ROM recovery.

More significantly, section 3.8 describes per-device code-encryption secrets
and hardware decryption. Download/debug access may be disabled permanently;
encryption NVM can be protected against reading/writing, with irreversible
permission changes. An arbitrary programmer or a successful UART electrical
connection therefore cannot guarantee a backup. No attempt to unlock, change
protection, inject a key or erase security state is authorized.
[Beken BK3633 datasheet V0.5, sections 3.1 and 3.8](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/doc/bk3633/Hardware/BK3633%20Datasheet%20v0.5.pdf).

The reviewed primary material does **not** confirm a BK3633/BK3533 tool mode
that always returns the complete physical encrypted flash byte-for-byte,
including CRC framing and reserved areas. It also does not establish whether
a given reader returns ciphertext, decrypted CPU-view data or a transformed
image. This question must be resolved for the exact loader/security state
before describing a dump as restorable. Protected NVM/eFuse is separate from
ordinary flash and may remain unreadable even if all flash can be copied.

## Why the existing app image cannot supply the missing backup

The local OEM updater resource is 134,140 bytes. Its transferred application
range does not cover the entire flash, boot code, protocol stack and all
device-specific nonvolatile contents. A generic production image would still
not necessarily contain this mouse's pairing/calibration/security state.
The flash capacity and complete partition map must be established from this
hardware; BK3633 documentation describes multiple capacities, not a verified
capacity for the G9.

The firmware also supplies an additional reason to avoid testing software
update entry casually. Its update handler at `0x39446` passes source `0x447EC`,
destination `0x7D000` and length 16 to `0x39CB0`. Independently, macro reader
`0x30FFA` reads `0x7D000 + slot * 0x80` through the same flash API. These are
the same address space, not a mistaken argument order. The helper saves and
rewrites 0x400 bytes while requesting a 0x1000-byte sector erase; its low-level
operation at `0x39924` matches the BK3633 SDK sector-erase encoding. Under that
SDK's 4 KB sector semantics, this risks the first macro and remaining sector
data; the G9's exact chip/erase geometry is not independently confirmed. The physical
effect has not been tested. Even application-triggered loader entry cannot
currently be treated as a harmless recovery probe.
[Beken flash API definitions](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/platform/mcu/bk3633/beken/driver/include/flash_pub.h).

## Next prerequisite decisions

1. **Identify the hardware.** Obtain the actual chip/package and board revision,
   plus manufacturer documentation or verified board photographs identifying
   download/reset access and electrical requirements. No G9 pad assignment has
   been established; none is prescribed here.
2. **Establish app-independent entry and its security limits.** Confirm the
   applicable ROM/loader handshake and whether it remains available when the
   app is invalid. Determine read-access policy without changing NVM/eFuse or
   flash protection. If access is permanently disabled, stop this route.
3. **Pin and audit an exact reader.** Obtain the recommended compatible tool or
   an equivalent documented reader. Inspect the selected chip's connection and
   read path, including automatic resets, boot-stub loading and protection
   changes. Do not infer safety from a button label or a generic read command.
4. **Define a complete, restorable representation.** Establish physical flash
   size, ranges, encrypted/plain format and CRC/address transforms. Include all
   accessible flash and record separately which NVM/eFuse regions are excluded.
   Plan two independent reads with matching hashes. App-only reads are useful
   evidence but do not meet the full-restoration requirement.
5. **Prove restoration separately.** A matching spare or an exact-hardware
   manufacturer recovery procedure is needed to validate restoration without
   making the only working mouse the destructive test fixture. A stable dump
   alone does not prove a compatible write/boot path.

The next useful step is therefore **hardware identity/access and exact-reader
qualification**, not another updater launch or a guessed HID read opcode.
Software-only research cannot clear those physical/security prerequisites.

## Local reproducibility record

Analysis downloads remain under ignored `evidence/recovery-uart/`; no tool,
vendor image or unique device backup is added to the public source tree.

| Artifact | SHA256 |
|---|---|
| Official SDK datasheet V0.5 | `2772e7ca7f9c253c478d9c8547100fce34cc99db0f86e8fd48f920fded9a4da5` |
| Official SDK serial-tool archive | `d90f432b2712416de50b71c4fb9db319089d793296710f9686b9dcf049ddbb69` |
| Official SDK USB-tool archive | `b953470c73a8bf29f3aaca25e06c3f21dc4258b588fd1d9bc18fd08b51e2a5a6` |
| Quectel-distributed bk_loader.exe 2.1.12.1 | `79d1d6e30eda7527099c7bdd2b2241488973b68623b4ecfbe87793a176c0d80b` |

The official SDK tree was pinned to commit
`70d28da4d0c97d45a119644d9eb9abd0db459b14`; `download-manifest.json` records
original URLs and hashes. Archive contents, PDF text, static executable strings
and disassembly notes are local evidence, not physical validation.
