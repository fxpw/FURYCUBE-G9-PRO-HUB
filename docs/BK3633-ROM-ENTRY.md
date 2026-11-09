# BK3633: boot symbols, installed revision and the remaining entry proof

2026-11-09. Bounded **offline** audit: no USB/UART connection, reset, reader or
vendor executable launch, protection change, erase or firmware installation.
Only the five replacement target photographs identify this unit: `BK3633QN48`
and PCB `EPC-V18U0002`, dated `2025-4-3`. The two earlier photographs were of a
different mouse and are excluded. See [hardware provenance](HARDWARE-IDENTITY.md).

**Result:** the chip family is identified, but the installed boot/stack revision,
silicon download-entry implementation and security state remain unidentified.
The already captured normal USB version response cannot answer those questions.

## `bim_rom_symbol.txt` is a build output, not immutable-ROM evidence

This can be checked directly in Beken's SDK at commit
`70d28da4d0c97d45a119644d9eb9abd0db459b14`:

1. The `BK3633_bim` target compiles `bim_app.c`, `bim_vectors.s` and
   `bim_handlers.s`. Its linker options explicitly contain
   `--entry sys_Reset --symdefs=.\lnk\bim_rom_symbol.txt --noremove`.
   Its post-build steps convert the linked AXF into `bk3633_boot.bin`, then
   invoke `encrypt_bim.bat`. These commands were **read, not executed**.
   [Official project file](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/platform/mcu/bk3633/beken/projects/boot/bk3633.uvproj).
2. The scatter file puts the vector/handler objects at address zero, followed
   by ordinary read-only object sections. `ROM_SYS`, `ROM_FUNC` and `ROM` are
   linker-region names. They do not establish a mask-ROM storage technology.
   [Official scatter file](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/platform/mcu/bk3633/beken/projects/boot/lnk/link_bim.txt).
3. The generated symbol list names those same functions: `vectors=0`,
   `sys_Reset=0x130`, `bim_main=0x9C1` and flash erase/write routines. This is
   evidence of an application-independent **mutable boot image (BIM)** build,
   not the implementation of a guaranteed silicon-ROM downloader.
   [Official generated symbols](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/platform/mcu/bk3633/beken/projects/boot/lnk/bim_rom_symbol.txt).

The SDK's `bim_main()` initializes UART and flash, selects an image, then calls
the stack entry. For an out-of-range selected address, its shown path loops
forever; it is not a demonstrated automatic transition into a silicon-ROM
recovery reader. This source is not a dump of the mouse's installed BIM.
[Official BIM source](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/platform/mcu/bk3633/beken/projects/boot/app/bim_app.c).

The same naming caution applies to this project's earlier “ROM helper” stubs:
a call below the app's base establishes an external ABI dependency, not that
the callee is immutable or matches this SDK revision.

## Which fingerprints can actually be checked now?

| Existing offline evidence | Established fact | What it does not identify |
|---|---|---|
| Downloaded SDK BIM source | Constants `0x36334B42` at CPU `0x100` and `0x00103333` at `0x104` are ordinary source declarations | Installed silicon ROM, security fuse state or a unique boot revision |
| Downloaded SDK boot BIN |3604 bytes; SHA256 `e93862c485d6fc9c83ff0b74eab635d4c79d5896880a018fedb7d8e986661c5d`; those constants and reset pointer `0x130` are present | Identity with the installed boot image |
| SDK OTA header declaration | Has a 16-bit field named `rom_ver` | A documented silicon identifier or a field exposed by the G9 version response |
| G9 app-only update and its reconstructed instructions | Normalized-artifact base convention `0x28790`; first actual ARM exception vectors at CPU `0x287A0`; preceding boot/stack code is absent from this resource | A hash of the installed boot or stack |

The constants are declared in [BIM source](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/platform/mcu/bk3633/beken/projects/boot/app/bim_app.c);
the header is in [bim_updataImage.h](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/platform/mcu/bk3633/beken/projects/boot/app/bim_updataImage.h).
Do not decode undocumented constant bits as current security permissions.

There is an additional artifact-consistency limit: the checked-in SDK map
reports a load size of `0x1564`, whereas its checked-in boot BIN is `0xE14`
bytes. The map/symbol/source/BIN set must not be assumed to be a reproducible
single build or used as an installed-image fingerprint without matching bytes.
[Official map](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/platform/mcu/bk3633/beken/projects/boot/lst/bk3633_boot.map).

For the G9's observed 32-data-byte plus 2-CRC-byte **code** framing, the byte
mapping `physical(a)=(a//0x20)*0x22+(a%0x20)` maps the actual vectors `0x287A0`
to `0x2B01A`. The OEM resource instead begins with a separate, unframed 16-byte
metadata prefix, written at `0x2B00A`. Do not interpret the normalized-artifact
base `0x28790` as a verified CPU address for that prefix: the code formula gives
`0x2B008` for it, not the updater's prefix position. Multiplying all byte
addresses by 34/32 is also wrong inside a code block. The first executable-byte
mapping relates the known app to its container; it does not prove encryption,
capacity or preceding boot/stack contents. The SDK header's app base `0x17A00`
also differs from the G9 vectors: its partition map cannot be copied directly.

## Why the existing normal GET0B is insufficient

The original G9 handler `0x33C8A..0x33C98` executes `MOVS r1,#1` and
`MOVS r2,#8`, then copies those two constants to the response. It reads no
boot/stack version, chip-revision register or NVM/security state. The captured
eight-byte report `0b 05 01 01 08 00 00 00` therefore identifies the app's
advertised version only. Its zero padding is not a protection-state bitmap.
The known HID descriptor's `bcdDevice=0x0108` is also an app-supplied descriptor,
not independent boot identity. See the [normal readback audit](USB-READBACK.md).

The already examined normal feature-report paths expose fixed settings,
macros and version constants, not an authenticated boot hash/security query.
No new feature selector or raw-memory request is justified by the chip marking.
The software update-marker path is also not a read-only identity operation;
its NVM write risk remains documented in [RECOVERY.md](RECOVERY.md).

## What is still needed without a boot probe

The datasheet describes startup selection between normal/program/debug modes
and allows permanent download/debug disable plus protected encryption NVM.
It does not supply, in the material reviewed here, a sufficiently specified
mode-selection command/timing and revision-specific recovery contract.
[Official BK3633 datasheet, sections 3.1 and 3.8](https://github.com/bekencorp/bk3633_mesh_sdk/blob/70d28da4d0c97d45a119644d9eb9abd0db459b14/doc/bk3633/Hardware/BK3633%20Datasheet%20v0.5.pdf).

Useful next evidence is exact-board service documentation or a matching full
production image with boot/stack map and version provenance; and a documented
non-mutating identity/security query for the applicable startup implementation.
Unpowered board photos can establish routing/access candidates, but cannot
reveal installed code hashes, fuse values or a guaranteed entry waveform.
No pin or reset instructions are inferred here from the chip/package alone.

A proven app-independent route must establish entry with the main application
unusable, permitted security state and the exact tool's connection effects.
It may rely on a preserved, compatible mutable BIM: app-only rollback does not
automatically require surviving BIM destruction or proving immutable-ROM entry.
That narrower route must instead prove the update preserves its BIM and other
required data, and that the exact original app can be restored through it.
Neither route is established merely by naming BK3633, BIM or UART. Recovery
remains unproven; firmware installation stays blocked.

Reproduction inputs and URL/SHA manifest are kept locally under ignored
`evidence/recovery-uart/rom-entry-audit/`; no vendor binary is published.
