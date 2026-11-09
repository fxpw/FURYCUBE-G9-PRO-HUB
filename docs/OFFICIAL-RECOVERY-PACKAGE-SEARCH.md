# Official package search: a separate G9 release, no qualified recovery package

2026-11-09. Read-only web/package research. No vendor installer/updater was
launched, no mouse/receiver interface was opened, and no USB/UART/reset or
firmware operation was performed. An external archive parser extracted files;
the vendor executables were parsed only as data. Downloads remain private under
ignored `evidence/official-recovery-search/`.

## New primary-source findings

The manufacturer's [G9 PRO support page](https://www.furycube.com/pages/g9-pro)
still links the already researched 1.08 update, but its current driver link is
`FURYCUBE_HUB_Setup_20260820.rar`. A separate official
[G9 support page](https://www.furycube.com/pages/g9) links `G9DPI_12000.rar`.
The latter is a new comparison input; it is **not** adopted for the user's
BK3633QN48 / EPC-V18U0002 board.

| Official-page link / package | New observed contents |
|---|---|
| [G9 firmware archive](https://cdn.shopify.com/s/files/1/0874/8626/7680/files/G9DPI_12000.rar?v=1751278056) | Folder `SH105 3311 OLED G9 BT 第一版 Ver1.04 Upgrade 20250424`; only `Update.exe` and `hidapi.dll` |
| [Current G9 PRO HUB archive](https://cdn.shopify.com/s/files/1/0874/8626/7680/files/FURYCUBE_HUB_Setup_20260820.rar?v=1787215968) | One Inno 6.1 installer; its extracted file list contains the HUB, HID/helper DLLs, UI resources and configuration data |

The G9 archive explicitly labels its package **first version** (`第一版`), while
the existing G9 PRO 1.08 archive labels itself **second version** (`第二版`).
These are manufacturer-supplied archive labels, not a proven mapping to PCB
revision or installed boot/stack. The current official
[driver directory](https://www.furycube.com/pages/drivers) links G9 PRO; the
separate G9 page must not silently replace that selection.

## What the newly found 1.04 updater actually supplies

Static PE resource enumeration found one firmware `BIN/129/2052` resource:

- 133884 bytes; SHA256
  `64132d75a3bac01d6b32fe34a2524100ee305b976fe166588fe71fd228b231af`.
- Header `29 da ff ee 23 00 bf 82 42 42 42 42 ff ff cc 6e`.
- The header CRC32 and word length match its complete framed body. UID remains
  `42424242`, the application-image UID, and ROM-version metadata is `6ECC`,
  as in the other official app update. That metadata does not identify the
  installed BIM or guarantee cross-revision compatibility.

No separately identified boot/BIM image, stack image, merged factory file,
emergency-entry instructions or current-device backup appears in this archive.
This is an earlier app-updater comparison input, not a newly established full
restore package. The 1.04 and 1.08 host `.text` sections differ, so this audit does
not claim their complete host workflows are identical or reuse 1.08 function
addresses for 1.04.

## Current HUB installer qualification

Its complete file list was read with innoextract 1.9; the installer itself was
not executed. No standalone `.bin`/`.hex`, factory image, `Update.exe`, recovery
manual or separately named BIM/stack payload was listed. The extracted
`FURYCUBE.exe` and seven `hiddriver_*.dll` files have no named firmware-resource
type in their PE resource directories. The seven HID DLLs are byte-identical.

This establishes the inspected package contents. It does not rule out every
possible dynamically downloaded feature or unrecognized encoding inside a
program. No additional qualified recovery path was identified from this HUB
package. The [archive parser](https://constexpr.org/innoextract/) operates
without running Windows setup code.

## Reproducibility and next decision

| Download or parsed file | SHA256 |
|---|---|
| G9DPI_12000.rar | `b2830c6deb80c0cd68024ec745926d8945e3dac1a490fb094c0e50bf27ce6b6a` |
| G9 first-version Update.exe | `2c1d8745efd5d23e365d01d377bb51cdd099031e89a04d92bd7dc874072a2692` |
| FURYCUBE_HUB_Setup_20260820.rar | `8e6526a3d5220c41ee886c46776b82025c3d3bbe5bbe972404d82ede37ec2aeb` |
| Its setup executable | `e91f8f0f6f3644a3ecab4d7ff5c86531c5c2fa9879200e79b45aad379d2829c1` |
| Extracted FURYCUBE.exe | `8ca24d9ed79a27b145a7c52a4d0dcc10e0bf1090c27af2646b6cff0be008cc55` |

Local URL/hash manifests, archive/file lists and resource/header comparisons
are saved with those downloads. Public searches covered manufacturer G9/G9 PRO
support/product/driver pages and indexed recovery/boot/factory terms in English
and Chinese. No official exact-board emergency-USB-entry procedure or factory
boot/stack package was found in this bounded search. That is not proof that
manufacturer service material does not exist.

The useful change to the plan is **explicit release/revision provenance**:
there are at least two differently labelled official G9-family app packages;
neither newly inspected package supplies the missing recovery proof. Preserve
the user's confirmed 1.08/actual-board context. Do not downgrade, install the
other model's update, infer a ROM/button-entry sequence or treat the shared
metadata as a recovery guarantee.

App-only rollback may still use a preserved compatible BIM/stack if an
independent USB entry and restoration of every affected non-app region are
established. This search does not establish those conditions. The
[recovery gate](RECOVERY.md) remains in effect.
