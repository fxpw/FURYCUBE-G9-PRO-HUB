# Hardware identity and public recovery evidence

Evidence through 2026-11-09: primary documentation, offline files and the user's
replacement target-unit board photographs. No agent programming connection,
vendor executable launch, bootloader entry or firmware change was performed.

## Result

The user confirmed that there is **no FCC ID on the bottom label**. Repeating
that request is unnecessary. The identifier itself is not mandatory; matching
the actual controller/PCB and proving independent recovery are what matters.
See [the updated recovery investigation](RECOVERY-NEXT-STEPS.md).

### Target-unit photographs now identify the package

The user subsequently opened the mouse and provided five replacement photographs
with a text transcription. The user explicitly said the preceding two reference
photographs showed **a different mouse**. Those earlier pictures are excluded
from target-unit evidence, including the previously tentative PCB reading.

| Observation on the replacement target photographs | Evidence / limit |
|---|---|
| Beken `BK3633QN48` | Legible IC top marking. The user's `8K3633...` transcription is resolved by the photo's `BK` marking and Beken logo. |
| `EPC-V18U0002` | Main-board silkscreen, supported by the user's transcription. Do not substitute the old tentative `EPC-M8U0002` reading. |
| `2025-4-3` | Printed board date; not a measurement of installed firmware age. |
| `PAW3311DB-T2MU` | Sensor top marking, consistent with the PAW3311 software path. |
| `A3311` | Additional board silkscreen, not a programmer-pad designation. |

This resolves the previous BK3533/BK3633 **package-identity uncertainty for this
photographed unit**: the physical marking is BK3633QN48. The manufacturer's
BK3533 marketing description below is not its observed top marking. The photos
do not identify installed ROM/BIM/stack revisions, protection bits or the actual
dump representation. They do not prove a restore path or read access.

No user board photograph or unit-specific IC lot marking is published here.
The supplied pictures show component-side routing; no continuity measurement
has mapped the download/reset signals to accessible board pads. A connector
reference such as `COM1` is not evidence of UART pin assignments.

The [Beken-authored datasheet V1.9, section 7](https://cionlabs.com/wp-content/uploads/2025/06/BK3633-Datasheet_V1.9.pdf)
lists QN48 as the 48-pin, 6 mm package with nominal 512 KB SiP Flash. That
ordering information is useful for qualification; a live flash identity/read
has not confirmed the installed capacity, accessibility or format. This later
datasheet also separates QN48/QN49 and records earlier USB/pin-table corrections;
do not use an older package diagram as a board wiring instruction.

The manufacturer's G9 PRO page and its firmware package identify the relevant
product/software family. They do **not themselves** establish service-pad routing
or an independent recovery entry. Package/board identity now comes from the
replacement target photos above. A specific FCC filing with internal photographs is a useful
lead, but its original photo/label PDFs could not be retrieved in this pass.
No recovery procedure or cross-brand firmware compatibility is established.

## Manufacturer evidence that was inspected

* The [official G9 PRO support page](https://www.furycube.com/pages/g9-pro)
  links firmware, the Windows HUB and the `bk-gmice.com` web driver. Its firmware
  link names `G9_PRO_BT_Ver1.08_Upgrade_20251028.rar`. The previously downloaded
  archive is present locally, with the extracted directory
  `SH105 3311 OLED G9 PRO BT 第二版 Ver1.08 Upgrade 20251028`.
  `SH105`, `3311`, `OLED`, and the second-edition wording are **package labels**;
  they are not a photographed PCB revision or a hardware recovery guarantee.
* The [official product page](https://www.furycube.com/products/g9-ultra-light-wireless-gaming-mouse)
  specifies PAW3311, a display for battery/DPI/link/polling status, three connection
  modes and a 500 mAh battery. It calls the controller **BK3533**, including in its
  [chip marketing illustration](https://cdn.shopify.com/s/files/1/0874/8626/7680/files/02_066f52f6-040e-4ae5-8828-a2cb3b3548ba.jpg?v=1754545471).
  This is not a photograph of the actual IC. The replacement target photos
  now resolve the physical package as BK3633QN48; marketing alone did not.
* The manufacturer's [exploded-view illustration](https://cdn.shopify.com/s/files/1/0874/8626/7680/files/08_52d2da3f-7000-418c-be0e-40285e2f43f2.jpg?v=1754549302)
  shows separate boards and a magnetic docking interface. It is a rendered
  illustration, with no legible board revision, MCU top marking, UART pad names
  or identified boot/recovery switch. It cannot establish a service pinout.

## Specific FCC leads, with access limits

The following are **indexed filing leads**, not a confirmed match to the
bottom label or PCB of the user's mouse. The FCCID.io pages are third-party
indexes; they are included to locate the original applicant submissions, not as
a substitute for inspecting those submissions or the actual hardware.

| Lead | Indexed applicant/date | What can be followed up |
| --- | --- | --- |
| [2BTAA-G9-PRO](https://fccid.io/2BTAA-G9-PRO) | FURYCUBE LIMITED; 2025-12-04 | G9-PRO filing; internal photos, label, manual and model-difference letter listed |
| [2BWN6-G9PRO](https://fccid.io/2BWN6-G9PRO) | Shenzhen Xiaobi Technology Co., Ltd.; 2026-06-30 | A different applicant/filing using G9Pro; do not merge with the first |

For **2BTAA-G9-PRO**, the useful exhibit identifiers are:

| Applicant exhibit | FCC attachment ID | Indexed mirror |
| --- | --- | --- |
| Internal photos, two pages | 8851912; alternate listed 8851898 | [Internal photos index](https://fccid.io/2BTAA-G9-PRO/Internal-Photos/Int-photo-8851912) |
| Bottom label/location, one page | 8851913; alternate listed 8851899 | [Label index](https://fccid.io/2BTAA-G9-PRO/Label/Label-and-Location-8851913) |
| User manual, two pages | 8851914; alternate listed 8851900 | [Manual index](https://fccid.io/2BTAA-G9-PRO/User-Manual/User-manual-8851914) |
| Model-difference declaration | 8851916; alternate listed 8851902 | [Declaration index](https://fccid.io/2BTAA-G9-PRO/Letter/Model-Difference-8851916) |

The [original FCC internal-photo attachment URL](https://apps.fcc.gov/eas/GetApplicationAttachment.html?id=8851912)
and the FCC.report PDF mirror returned HTTP 403 locally. FCCID.io provided
searchable index metadata, but its PDF routes were inaccessible. Consequently,
**no FCC-submission PCB photograph, service pad or declaration content was
visually verified** in that retrieval pass. The user's later target photographs
are independent evidence, not a successful FCC attachment retrieval. The index reports internal-photo SHA-256
`1f5be1aafe34e2a2f0deab040b6f53198cc8f635d12b7ae3247c2aaa083499df`;
this is a retrieval aid, not a locally verified file hash.

An [optimized mirror of the applicant's manual PDF](https://device.report/m/237610378d312d2dadfc5a63ad93b569310b64a92f4226b9db9c163ded2e972b_optim.pdf)
was accessible to the web reader. Its extracted text is mostly the FCC notice;
the remaining controls are graphic content, so the text extraction is insufficient
to prove any recovery-button sequence. The index associates the original manual
with SHA-256 `237610378d312d2dadfc5a63ad93b569310b64a92f4226b9db9c163ded2e972b`.
The optimized mirror must not be expected to have the original file's hash.

Both filing indexes list schematics, a block diagram and operational description
as **metadata only**. No public schematic or service manual was obtained.
The newer 2BWN6 filing lists internal photos with a future availability date of
2026-12-28; its unavailable photographs are not evidence about the older board.

## What remains unproven

No inspected primary material maps the SH105 package label or USB
`1D57:2148` / `1D57:FA60` identifiers to either FCC filing, to an exact PCB revision,
or to a different brand's compatible product. Similar shape, PAW3311, a display,
or a shared Beken family is insufficient for firmware compatibility.
Shenzhen Xiaobi is only an applicant lead for a similarly named product;
it is not established as the OEM of this mouse.

No inspected manufacturer page or accessible primary document identifies a
hardware boot strap, reset-to-ROM button, test-pad voltage, UART wiring or a
boot sequence independent of the running application. This is a bounded search
result, **not proof that those facilities are absent**. Pairing, DPI selection,
screen control, factory-settings reset and USB firmware update are not themselves
proof of recovery after a damaged application image.

The chip and visible board identifier no longer need another photograph merely
to read their names. The next missing evidence is the actual board's download/
reset routing, electrical conditions and compatible non-mutating reader/entry.
This needs a matching board schematic, independently checked continuity or
manufacturer information; no pad assignment is guessed from a nearby label.
Until that chain and a usable full restore image/readback are established, the
recovery gate in [RECOVERY.md](RECOVERY.md) remains closed.
