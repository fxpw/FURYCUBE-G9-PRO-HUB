# Radio OLED candidate matrix

Updated 2026-11-20. These are separate, pinned development candidates for the
identified G9 PRO. OL9 has been installed over USB. The complete wired profile
matched the backup after restoring the screen button, and wired input passed
9/9. The radio frame test follows this check. OL7 is the last build with a
visually confirmed USB image. OL8 has passed offline checks.

| Variant | Purpose | Carrier | Status |
| --- | --- | --- | --- |
| OL7 | Baseline with USB settings/input readiness guard |56-byte SET04, kind0, stage0, custom CRC/tag6 | Earlier installed; USB image/input confirmed. After physical reconnect GET0C worked, but sensor GET04 and later version probes timed out. Passive radio input passed 9/9; no radio image submitted. |
| OL8 | Test whether the receiver supplies context kind1 | Same carrier; allow kind0/1 and reject2/255 | Full offline checks and bounded updater ready; not installed. |
| OL9 | Test a receiver that verifies the native sensor checksum |56-byte SET04 with OEM checksum50..51 plus custom CRC52..53/tag9 | Installed over USB. Firmware marker, profile and wired input verified. Radio frame pending. |

OL8 changes exactly three compiled bytes relative to OL7: the version marker,
kind comparison and rejection branch. OL9 is described in [its packet
specification](OL9-CARRIER.md). These hypotheses concern OLED delivery. They
do not explain or repair a failed ordinary GET0C transaction by themselves.

All variants retain the ordinary screen-key fix, profile1 validation, native
settings dispatch, complete-frame staging, pixel ownership and the USB EP2
guard. Custom uploads do not add per-frame flash writes. The production HUB
continues to block receiver OLED until end-to-end delivery is qualified.

## Pinned artifacts

| Variant | ARM bytes | Resource bytes | Resource SHA256 |
| --- | ---: | ---: | --- |
| OL7 |2032 |136120 |`bdb11da81885db47f7498f63643c1750e685eeca701e6054266ff4787e894f24` |
| OL8 |2032 |136120 |`b31b1a65f45cb84b68ab634f278e5d059586625af1dde8202df875fa957195e7` |
| OL9 |2080 |136152 |`385a653c071cf509d376f8efe2c5d96333cfc68ee7a4d13f7f2b35b8a18dab0e` |

Bounded updater SHA256:

- OL7:`44bfc10c87433ea8b5de2da7273d6e7e8d6bd2a3d82c7fd8ae286370da1683b8`
- OL8:`7f35088b0c6697898f25e168cadfbb556020cc51ac479fe5ce9e54f858bddcf1`
- OL9:`1f7a34bf8ce51cbc9c0be9072b0b49e5e78849260965734539e3b03cb972980c`

Private output stays under ignored `patch-output/`. Manufacturer firmware and
updaters are not distributed in this repository. Each package independently
preserves 748 other resources, original raw offsets/mapped pages and all code
outside two reviewed host patches. Actual resource preparation and 34-page
programming are emulated; the erase interval remains `[0x2B000,0x51000)`.

Each complete decoded candidate passes 800 wired IRQ cases, 53 endpoint cases,
47 settings/input cases, 34 invalid-carrier rejects, complete 480-byte native RF
assembly/rendering and the native pixel/sleep/retained-reset suite. Physical
receiver delivery, timing and actual SRAM retention remain outside these models.

## Next physical session

1. Switch the OL9 mouse to 2.4 GHz. The updated native settings transport
   already read and verified all six archived radio records on DG4.
2. Submit one control frame with the guarded `ol6-radio-frame` CLI. It checks
   the firmware marker and active profile before choosing the OL9 packet.
   The USB feature buffer is padded to 134 bytes and the logical packet remains
   56 bytes. Stop on a failed request and collect the result before continuing.
3. Check the physical picture and ordinary input, then compare the readable
   settings against the USB snapshot. OLED remains experimental on radio.

The last fully verified logical profile is 117 bytes with SHA256
`7ffa0a87f04a79e52b4f4a519485f0ddbc0098ca5c9f94c0e8dcf8a2cea8d643`:
profile 1, stage 2 / 1600 DPI, 1000 Hz and ordinary key 0 on the screen button.
Radio snapshots cover fewer records and must not be called a complete
117-byte backup.
