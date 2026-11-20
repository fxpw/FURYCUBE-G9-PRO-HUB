# OL9 checksum-compatible SET04 carrier

OL9 is a separate mouse-side experiment derived from immutable OL7. It preserves the ordinary SET04 sensor checksum location and calculation while carrying forty OLED bytes in the same 56-byte RF report. It does not establish that the receiver will relay this envelope, and must never be sent to an unmodified mouse. The checksum shape matches `MouseSettingsCodec.BuildSetPacket` in `src/G9MouseSettings.cs`; this envelope is deliberately not valid native sensor settings (stage0 and custom tail).

## Packet

All offsets refer to the complete56-byte packet. The stock RF body excludes reportID/length and travels as30+24 bytes.

| Offset | Meaning |
| --- | --- |
| 0..2 | `04 38 01` (SET04,56 bytes,profile1) |
| 3..5 | Fixed `00 01 3F` |
| 6 | Page0..3 |
| 7 | X0,40,80 |
| 8 | Initialize0/1;1 only at page0/X0 |
| 9 | Count40 |
| 10..23 | Pixel0..13 |
| 24 | Invalid native DPI stage0; claims custom handler |
| 25..49 | Pixel14..38 |
| 50..51 | OEM unsigned sum of bytes3..49, big endian |
| 52..53 | CRC16/CCITT-FALSE over bytes0..51 followed by pixel54, little endian |
| 54 | Pixel39 |
| 55 | Revision9; independently claims custom handler |

The RF hook claims stage0 OR revision9 before the native setter. It then requires report04, exact RF RAM body pointer, length56, context kind0, RF state9, remaining0, sequence2, fixed fields, stage0, revision9, correct OEM sum and custom CRC, and both active/requested profile1. The common GO4 validator enforces page, X, count, initialization placement, and last-page pixel high-nibble rules.

CRC validation saves the expected16-bit value, copies pixel54 into byte52, and invokes the seed-resetting contiguous CRC routine once over53 bytes. Reconstruction first preserves header fields, copies pixel54 to51, moves25..49 to26..50 backward, then moves10..23 to12..25 backward. Only then does it write the normal64-byte GO4 header, tail, and CRC for the shared staging/store/renderer.

All OL7 wired callbacks, EP2 settings guard, NoWait patch, native pixel ownership, sleep policy, retention store, legacy dispatcher and RF prefix restrictions are retained. Version marker/symbols are OL9. Normal captured SET04 with nonzero sensor flags takes the original dispatcher with the same register arguments.

## Artifacts and checks

- New source: `firmware/g9_ol9.S`, `tools/emulate_ol9_carrier.py`, `tools/build_ol9_candidate.py`.
- Payload2080 bytes, SHA256 `1162dce22978468529baffe68f1b8a6b9eb868ee0b2c9b59f268a565f3d4f138`.
- Raw sealed resource136152 bytes, SHA256 `385a653c071cf509d376f8efe2c5d96333cfc68ee7a4d13f7f2b35b8a18dab0e`.
- Framing:4004 CRC blocks total,65 appended blocks,28 changed original blocks, no final FF alignment bytes; transfer rounds to34 pages (`0x22000`).
- Full480-byte upload and native twelve-tick rendering, partial-frame retention, malformed fields/checksums/tag/stage, valid-checksum semantic rejects, OEM mismatch with valid custom CRC, legacy register equality, and inherited native/IRQ/settings regressions execute offline in Unicorn.

Run under WSL from this repository:

```sh
tools/pyenv/bin/python tools/emulate_ol9_carrier.py
tools/pyenv/bin/python tools/build_ol9_candidate.py
```

The builder pins the payload and sealed resource independently, verifies original resource/header/block framing and all inherited checks, then creates a uniquely named raw `.CANDIDATE.bin` and manifest under `patch-output/`. Baseline fixture compilation rewrites shared generated `bin/g9_ol5.o` (and settings regression OL6 artifacts), so serialize full builds with other variants.

The subsequent PE packaging review is complete. `tools/ol9_updater_package.py`
pins the resource and resulting updater independently; the resource fits the
original raw section without moving file offsets or mapped pages. It preserves
748 other resources and changes only the reviewed copy-length/erase-length
instructions plus the resource extent headers. `tools/emulate_ol9_updater.py`
executes the exact x86 preparation and 34-page transfer with hardware APIs
stubbed: no resource overread, unchanged erase interval `[0x2B000,0x51000)`.
The disabled updater SHA256 is
`1f7a34bf8ce51cbc9c0be9072b0b49e5e78849260965734539e3b03cb972980c`.

OL9 was installed over USB on 2026-11-20. The radio frame submission left the
physical display at 1600, with input and saved settings verified afterward.
The owner confirmed a visible screen change when the same frame was sent
through USB to the same OL9 installation.
The [candidate matrix](RF-CANDIDATE-MATRIX.md) records the physical test and the
replay of its exact reports. Independent firmware recovery remains unverified.

The earlier, readback-verified DPI change through FA60 passed a 56-byte SET04
buffer to Windows. The unsuccessful OL9 frame used a 134-byte buffer. The
guarded frame CLI's `--logical-feature-length` comparison sent exactly the
same twelve carrier payloads in 56-byte feature calls. The image reappeared
on the OLED after the switch from USB had returned it to 1600. The passive
input check passed 9/9 and all six readable settings records matched the
USB backup.

Production sends 56-byte OLED feature calls with a 500 ms gap after each
fragment. Stream discovery verifies OL9 and active profile 1 once per
receiver session. The mouse checks the active profile in its carrier
handler. Version, profile and sensor reads retain their 134-byte prepare
framing. The HUB renders numeric DPI from the read-back sensor settings
and passive DPI-stage notifications. Periodic frames skip control reads.
