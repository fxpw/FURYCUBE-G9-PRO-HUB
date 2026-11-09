# Image header correction and offline validation

Updated: 2026-11-09. No candidate has been installed on the development mouse.
Recovery remains unproven. These checks concern file consistency, not physical
boot acceptance or device compatibility.

An earlier builder preserved all 16 header bytes after patching the body. That
was incorrect. The pinned official 1.08 resource has this layout:

| Offset | Field | Original value |
|---|---|---|
| 0..3 | Body CRC32, little endian | `F0D4F2EA` |
| 4..5 | Version | `0023` |
| 6..7 | Total resource length in four-byte words | `82FF`, or 134140 bytes |
| 8..11 | Application UID | `42424242` |
| 12..13 | CRC/sector status | `FF FF` |
| 14..15 | ROM-version metadata | `6ECC` |

The CRC32 field exactly equals `zlib.crc32(resource[16:]) ^ 0xFFFFFFFF` on
the official resource. This is reflected polynomial `EDB88320`, initial value
`FFFFFFFF`, without a final XOR. Coverage includes the framed body's CRC16
checkwords and all final FF padding. It excludes the 16-byte header.

The field layout and CRC primitive also occur in the inspected SDK mirror's
[`img_hdr_t`](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_updataImage.h#L29).
That source is not the installed G9 loader: its image addresses and some CRC
coverage/length rounding differ. Matching the official file and primitive does
not establish which checks the actual G9 boot path performs.

The builders now validate the official header, recalculate body CRC32 and word
length after framing, preserve the other ten header bytes, and validate the
result. OL3 adds two final FF bytes for four-byte alignment. Code addresses,
payloads, original block boundaries and the rounded `0x21000` transfer length
stay unchanged.

| Candidate | Corrected bytes | Corrected resource SHA256 |
|---|---:|---|
| NoWait | 134140 | `01908b65a972ae04282392088e2581640365a0d0712c9f281d6f54691ea14865` |
| OL2 | 134248 | `08b277e7b2a6941ac1abe05a53bd36a9836310915461efca610fc98211d22ad0` |
| OL3 | 134624 | `62ad25dadbc5b3d0fe6ca4383c81ce6259f488c70c20793b568be6e93a8561fb` |

The old NoWait, OL2 and 134622-byte OL3 candidates have stale CRC32; both OLED
variants also have stale lengths. Their former whole-resource hashes are
superseded. Existing local artifacts are retained as research history; rebuild
with the corrected source. Do not install any candidate before recovery is
established.

C# and Python implementations independently produce matching resource hashes.
Regression checks reject the old preserved-header form, body corruption with a
stale CRC, a stale word length, truncated and unaligned resources. An independent
synthetic CRC vector runs without distributing any manufacturer binary. ARM
behavior checks use bytes decoded from newly framed candidates; they do not
exercise the installed loader or flash hardware.
