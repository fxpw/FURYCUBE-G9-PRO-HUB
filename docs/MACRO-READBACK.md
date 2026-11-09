# Stock logical macro-slot readback

Offline audit and codec, 2026-11-09. No mouse USB/UART transaction, loader entry, reset or macro upload was performed. This concerns **normal HID report 0x09**, not the unrelated UART page-read opcode 0x09. The Windows virtual-key macro editor JSON is a separate host format.

## Exact request and logical response

For a **one-based slot 1–32**, the stock A0 read query is:

```text
A0 09 6F 00 <slot> 00 00 00
```

Selector 0 skips the requested flash load and uses existing RAM. A selector beyond 32 can alias slot 1 after firmware clamping. Neither is accepted by the new codec.

| Logical response offset | Bytes | Meaning |
|---|---:|---|
| 0 | 1 | Report ID `09` |
| 1 | 1 | Logical length `6F` = 111 |
| 2 | 1 | Exact requested slot, 1–32 |
| 3–108 | 106 | Opaque macro data, preserved byte-for-byte |
| 109–110 | 2 | Sum of the 106 macro-data bytes, uint16 **big-endian** |

The checksum is an additive sum, not a polynomial CRC. No event/key interpretation or conversion to Windows VK is inferred. For a valid readback the complete logical frame is exactly **111 bytes**. The normal descriptor declares report 0x09 as **64 feature bytes**. That mismatch must be qualified at the transport layer; the codec does not concatenate arbitrary fragments, accept truncation, strip unknown padding or treat a descriptor as proof that Windows/receiver long reads work.

## Stock path and bounded side effects

The pinned normalized stock 1.08 image SHA256 is `e0cc9bcb8930ea430d8bc14fefb746f189f0f5f743b81c0b32a5b161f7fba5db`.

- The read FSM `0x34294..0x3431C` queues selector-1 for the special macro case at `0x342EA`; it schedules worker command `0x29`, not a profile store or macro write.
- Worker `0x313D8..0x313EC` calls macro loader `0x30FFA`. That loader clamps zero-based indices >=32 to 0 and reads **108 bytes** using `0x39BC2` from `0x7D000 + index*0x80`, through `0x7DF80`. It then copies the record into shared macro RAM at `0x4010AE`. It performs no checksum rejection or flash write itself.
- GET data case `0x33C40..0x33C6A` copies the 106 data bytes, converts the RAM checksum word to big-endian output and returns 108 body bytes. The normal stored macro checksum word is native little-endian; logical GET bytes must not be labelled a raw physical flash copy.
- The common read tail `0x33C9A..0x33CA6` reloads the active profile through `0x30B78`. Its bounded read/section-validation function ends at `0x30D84`; invalid settings can fall back to RAM defaults. The separate function starting at `0x30D86` is a writer and is not reached by that read tail.
- A0 submission and data construction run wake/settings logic. The macro selector and shared macro RAM are changed. This is **not** passive telemetry or a guarantee about concurrently running macros, input behavior or power state.

An independent offline run used the existing `FlowFixture(b'', {}, patched=False)` with **original unpatched instructions**, synthetic global/profile/macro flash and guarded flash routines. It executed all **32 selectors consecutively** through the real A0/HID/read FSM, worker `0x3136C`, macro loader, GET formatter and active-profile loader. Each returned exactly the expected slot-specific 111-byte frame and checksum. Observations: **32 macro reads + 32 active-profile reads, zero flash writes, zero global-record persists**; active profile and Update_Profile remained unchanged. Macro selector ended at 31, and 64 wake calls occurred. Hardware/ROM transfer boundaries were stubs; this is not a physical read-access or recovery test.

This supports a narrowly scoped logical read on a **quiescent** stock state. It does not authorize overlapping queries or draining pending macro/profile operations. A future transport must serialize the shared request/worker state, start without a pending response, send one prepare, use bounded ACK observation, consume the expected response once, and abort on any mismatch. [Known A0/body freshness limits](READ-HANDSHAKE.md) still apply: ACK1 plus a new header does not establish a fresh body. Slot/checksum checks cannot eliminate every possible stale-body coincidence.

The historical local `protocol-wired-macro-before.txt` illustrates that limit: a request for slot 13 received a 111-byte transfer beginning `0B 05 01 01 08`, a stale version response. It is **not** a validated macro capture. No newly acquired physical macro slots are claimed by this work.

## Pure codec and honest archives

The later [storage-preservation audit](MACRO-STORAGE-PRESERVATION.md) executes
the original setter and confirms full 128-byte writes. Its temporary bank is
the state before the last update, not a current raw backup. Logical slot
readback cannot reconstruct the unexposed 20 bytes from that older copy.

`src/G9MacroBackup.cs` has no transport or upload capability. `MacroReadbackCodec.Decode` requires the exact ID/length/selector/checksum above; `BuildReadRequest` only constructs the bounded query bytes and does not add them to the live device whitelist. `MacroSlotRecord` owns immutable copies of raw 111-byte frames and opaque 106-byte data.

`MacroBackupArchive` saves validated slots as fixed relative raw/data files plus a hash-checked manifest. Missing slots remain explicit; duplicate slots or invalid checksums cannot silently replace records. An erased-looking all-FF record with stored FFFF does not pass its data checksum and is never fabricated as an empty macro. A partial archive remains useful evidence; a complete logical set means only that all 32 expected slots were supplied and validated.

The firmware flash-API slot stride is 128 bytes, while this read path exposes 108 per slot: **20 bytes per slot are not exposed**. All archives explicitly retain `isPhysicalMacroBankDump=false`, `isFirmwareBackup=false`, `transportQualified=false`, `physicalCaptureVerified=false`, and `restoreProven=false`. The component does not acquire device data itself. Offline fixture/import provenance is recorded; no actual-unit capture is included. Firmware, boot/stack, global/pairing/calibration state and physical encrypted/CRC framing are outside this archive. No macro restore/upload method is implemented.

Validation: `tools\build-macro-backup-tests.cmd` — **PASS 98**, synthetic/offline only. Tests cover independently specified checksum bytes, all 32 request selectors, wrong/stale/truncated/padded frames, erased-slot rejection, immutable buffers, partial/full logical coverage, portable relative files, duplicate/escaped JSON members, path traversal, file hashes, checksum/data cross-checks and filesystem failures.
