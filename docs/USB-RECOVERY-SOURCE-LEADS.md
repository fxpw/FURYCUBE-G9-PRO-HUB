# USB recovery: bounded source-code leads

Updated: 2026-11-09. Offline source reading and isolated original-C host fixtures:
selected source functions were compiled with mocked registers and I/O. No full
vendor program or MCU image was built or run, and no compiled vendor program
or firmware was downloaded from these leads or sent to a device. Only selected
text sources and repository trees were cached. No USB/UART access, loader
transition or reset was attempted.

**Useful new lead:** one BK3633 SDK mirror contains an `A745:0033` USB boot
project with the same HCI command namespace as the OEM updater. It is a concrete
lineage/comparison target, not proof of the G9's installed bootloader, a working
dump implementation or an approved recovery procedure.

## Repositories and pinned scope

| Repository / inspected commit | Result |
|---|---|
| [brkNx/vxe-r1-firmware-fix](https://github.com/brkNx/vxe-r1-firmware-fix/tree/e09c5127b7592a82cc6c043c8ecb14825149e85c) | Four files: README, license, image and firmware. The README reports VXE recovery at `3554:F406`; there is no recovery-handler source. Its claimed CompX/Beken equivalence and hardware-ROM entry are not established by code here. No VXE binary or button sequence is adopted for G9. |
| [j-friday/dongle2](https://github.com/j-friday/dongle2/tree/e11f67a7f8cfcecd1e7a00a982872dbd468dc018) | The sole listed branch, master, has 233 files: TeenyUSB with STM32/CH56x drivers and samples. The description says BK3633; this inspected tree does not supply a BK3633 USB recovery/read handler. Its three submodules are TeenyDT, STM32 driver and PC test tool. Historical/unpublished code is outside this result. |
| [j-friday/bk3633_base](https://github.com/j-friday/bk3633_base) | Empty repository: GitHub page says empty, branches API returns an empty list, commit API returns HTTP 409. No source to inspect. |
| [himaexternal/BK3633_DesignKit_V06_220F](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/tree/7720ca89ae8dbe26bc37942a33c675c97f127509) | 1,625-file tree; inspected the 36 text/source files selected under `projects/app_2.4g/boot_usb/`. This is a third-party SDK mirror, not an authenticated G9 factory image. Output executables/images were excluded. |

Local trees, selected source bytes and SHA256 download manifest are under the
ignored `evidence/recovery-source-leads/` directory. Default-branch observations
are pinned above; they are not claims about every historical repository state.

## Matching USB identity and namespace

The mirror's [`usb.h`, lines 51–52](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/usb/usb/usb.h#L51)
defines **VID `0xA745`, PID `0x0033`**. Its
[`usb_desc.c`, lines 32–48 and 91–97](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/usb/usb/usb_desc.c#L32)
declares 64-byte HID input/output without report IDs, using interrupt IN/OUT.
This matches the identity/envelope expected by the G9 OEM updater more closely
than the unrelated `10C4:0033` SDK adapter in
[USB-LOADER-READBACK.md](USB-LOADER-READBACK.md).

`bim_app.c` parses `01 E0 FC`, extended `FF F4 <lengthLE16>`, and flash
subcommands including `0x07`, `0x09` and `0x0F`; responses use `04 0E ...`.
This establishes a matching namespace, not byte identity with installed G9 code.
[Source parser](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_app.c#L162).

## Read and entry findings, with limits

- **Read `0x09` is present but incomplete:** addresses below `0x20000` are
  rejected. The accepted branch reads **16 bytes**, declares response length
  **4102**, but its response helper transmits only **64 bytes**. No complete
  4 KiB page transfer is implemented there.
  [Read branch](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_app.c#L267),
  [response helper](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_app.c#L133).
- **Some app-independent fallback exists in this source:** an existing `1234`
  marker selects USB. Without it, wrong app UID or an all-`FF` final 256-byte
  block writes the marker at `0x7D000` and starts USB. This is not a general
  invalid-app CRC fallback; arbitrary bad application code retaining its
  header/tail is not covered.
  [Boot decision](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_app.c#L478).
- **Verification is not read-only here:** command `0x10` changes protection,
  erases the `0x7D000` sector, calculates CRC and requests reset.
  [Verification handler](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_app.c#L80).

Other source details prevent treating this as a drop-in G9 recovery guarantee:
[`bim_updataImage.h`](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_updataImage.h#L7)
uses a different layout (`SEC_IMAGE_RUN_APP_FADDR = 0x280B0`, versus the OEM
G9 app transfer's `0x2B00A`). The final `0x4B60` handoff is an executable
image/stack entry; it must not be labelled a direct jump to the application.
Also,
[`flash_wp_8k`](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_flash.c#L152)
can write flash status/protection registers. Thus even this candidate boot path
cannot be assumed to preserve outside-app NVM/security state without analysis.

## Reproducible source-derived fixtures

[`tools/audit_boot_usb_source.py`](../tools/audit_boot_usb_source.py) pins ten
text-source SHA256 values from the commit above. It requires the already cached
sources and existing WSL `cc`; it performs no download or device access:

```text
wsl -e /mnt/d/repos/fxpw/test/FURYCUBE-G9-PRO/tools/pyenv/bin/python tools/audit_boot_usb_source.py
```

The two kinds of evidence are explicitly separated:

- **Compiled original C:** the original read parser, response helper,
  `flash_read` and `flash_read_data` bodies are extracted and compiled into a
  temporary host test. Flash registers/FIFO are synthetic, USB TX is a memory
  sink, interrupts are inert, and any erase/program helper aborts the test.
  The flash-read declaration is renamed only to interpose a call logger.
  AddressSanitizer and UndefinedBehaviorSanitizer were enabled.
- **Labelled boot model:** six Python scenarios model the pinned source's
  boot decisions and the erased-backup selector branch. Exact-source anchors,
  full-file hashes and a main-call inventory guard the model. This does not
  execute ARM startup, image code, interrupts, GPIO or a complete physical boot.

### What the positive read primitive actually provides

All seven compiled cases passed. Length is **bytes**, not words:
[`flash_read_data`, lines 347–383](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_flash.c#L347)
copies one byte and decrements length each time. It aligns controller accesses
to 32 bytes, then copies from the requested byte offset. The higher
[`flash_read`, lines 529–553](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_flash.c#L529)
splits accesses crossing a 256-byte page.

| Fixture address | Result in the compiled source with mock FIFO |
|---|---|
| `0x1FFF0` | Status 6; no flash read |
| `0x20000`, `0x20010` | Exactly 16 correct consecutive bytes each |
| `0x20001` | Unaligned request returns the correct 16 bytes |
| `0x2001F` | Crossing a 32-byte boundary returns the correct 16 bytes |
| `0x200F8` | Crossing a 256-byte page returns the correct 16 bytes |
| `0x80000` | Accepted by the handler's missing upper-bound check; synthetic result only, not proof of installed capacity or valid hardware access |

In each accepted response, bytes **11..14** echo the address and **15..30**
contain the correct 16 data bytes. Bytes **31..63** remain the poisoned old
global-buffer tail. The long response copy does not overwrite the valid 16;
it falsely advertises 4 KiB and copies stale data after them. The fixture-only
decoder extracts just those 16 bytes and rejects wrong headers, addresses,
status and transfer length. No transport or command-encoding CLI is exposed.
The synthetic compiled-C vectors, including the error response, are cached in
`evidence/recovery-source-leads/boot-usb-read16-compiled-fixtures.json`.
Undefined error-response stack tail is explicitly replaced by `DD`; successful
stale tails are the intentional `CC` poison fixture. A source-specific parser
must reject the different **4,111-byte UART page reply**, never silently treating
it as this 64-byte USB message or retaining the stale tail as flash data.

This is a useful **source-level USB read16 primitive above `0x20000`**. It could
support 16-byte stepping if an installed compatible handler and a safe entry
were separately established. It cannot read the lower 128 KiB through this
branch, establish raw/encrypted dump representation, or preserve bytes already
changed during entry. It is not a tested G9 read capability.

### Actual pure codec and its separate tests

[`tools/usb_boot_read16.py`](../tools/usb_boot_read16.py) now provides
`encode_logical_request` and `decode_logical_response`, with no transport,
device opening, file acquisition or CLI. Its provenance identifies SDK mirror
commit `7720ca89ae8dbe26bc37942a33c675c97f127509`; the audited `bim_app.c`
SHA256 is `a22f5ef7918c105ec80a4ea9acee0b626b0e65170ca214cbe44f080675533550`.
The independent source-audit tool above pins the other driver/header hashes.

Both codec methods require an explicit `declared_capacity`: it must cover at
least `[0x20000, 0x20010)` and cannot exceed the **24-bit source controller's
16 MiB address space**. The complete 16-byte span must satisfy
`0x20000 <= address <= declared_capacity - 16`. The bus ceiling prevents address
bits overlapping the source's controller opcode bits; it is **not the installed
flash capacity**. No QN48 capacity default or live access is inferred.

The decoder accepts exactly 64 logical bytes, the source-specific header,
success status and exact requested address, retaining only the first 16 read
bytes. A full-response hash records observation provenance; the stale tail is
not retained as flash data. It rejects OS HID envelopes, alleged long pages
and UART 4,111-byte replies. All compatibility/backup/restore flags remain false.

[`tests/test_usb_boot_read16.py`](../tests/test_usb_boot_read16.py) has **seven
unittest groups**, covering bounds/types, headers, failure status, wrong address,
tail isolation and envelope rejection. These are separate from the **seven
compiled-original-C cases** and **six labelled boot-model cases** above.

```text
python -m unittest discover -s tests -p test_usb_boot_read16.py -v
```

All seven unittest groups passed. An additional comparison against the cached
compiled-C vectors accepted five in-range successful replies and rejected the
source's below-bound failure and its synthetic `0x80000` reply under an explicit
fixture bound of `0x80000`. This is format/control-flow validation, not hardware
qualification or permission to issue the constructed bytes.

### Full main path and the executable-crash counterexample

The inspected `bim_main` initializes ICU/watchdog/UART/flash/RF, reads the marker,
performs the header/tail decision, then enables interrupts and runs its command
loop. With no marker, the `check_cnt++ > 2` branch exits toward `bim_select_sec`;
it does **not** start USB. A marker with byte 2 equal to `0x55` permits a later
counter-based exit; other markers wait. The counter has no calibrated time
unit here. No manual GPIO/chord/USB-presence entry decision occurs in this main;
the checked `bim_gpio.c` contains no implementation.
[Pinned main](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_app.c#L478).

The selector separately handles backup images. For the fixture's **erased
backup**, it returns success without checking the current image, then main
hands off to `SEC_IMAGE_RUN_CADDR`. Other selector branches can restore an
image or erase backup storage; they do not provide a manual USB-entry branch.
[Selector](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_updataImage.c#L460).

The explicit counterexample holds the UID, length and non-erased tail constant
while the eventual executable-image/application behavior is a simulated crash.
The model still reaches the same handoff, with **zero USB initialization and
zero marker writes**. Thus a metadata-preserving bad patch is not covered by
the demonstrated header/tail fallback. The crash is modelled after handoff;
no ARM application was executed. This result concerns this source path, not
every BK3633 ROM, stack or SDK version.

Outside-app effects are recorded rather than ignored: both failing-metadata
branches call `flash_write(..., 0x7D000, 16)` plus `flash_wp_8k`; startup itself
calls `flash_wp_256k` through
[`flash_advance_init`](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_flash.c#L292).
These protection helpers can write status registers. This is the SDK source's
16-byte marker call, distinct from the previously audited G9 application's
sector-erase/read-modify-write transition. No NVM operation was performed by
the test: all such hardware effects are recorded/modelled or forbidden.

## What this changes next

The search now has a concrete source fingerprint to compare against a
**matching G9 loader/BIM image or manufacturer specification**: descriptor,
HCI dispatch, marker handling, fallback conditions and register operations.
That is more specific than searching for arbitrary HID feature IDs. No
speculative read command or firmware installation follows from the match.

The open recovery question is whether the installed G9 boot component offers
an entry that survives the relevant main-app failure **and** preserves or
restores every affected outside-app byte, allowing the known stock app to be
restored. Complete flash backup is one route; verified app-only rollback is
another. This mirror alone proves neither. It does not make USB recovery
impossible. The source-specific pure codec does not establish a qualified G9
backup path.

No VXE button combination, alternative firmware or compiled SDK loader is
recommended for the G9. There is no change to the live device command whitelist.
