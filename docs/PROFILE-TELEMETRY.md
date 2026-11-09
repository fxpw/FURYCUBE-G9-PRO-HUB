# Passive profile notifications and numeric DPI binding

Updated: 2026-11-09. These findings come from offline execution/disassembly of
the local stock application, with effective image base `0x28790`. No new GET,
SET, feature allowlist entry or USB operation was introduced. Physical receipt
of a profile notification has not been tested.

## Exact stock packet

The wired USB path constructs this five-byte input report:

```text
03 C9 80 p 00
```

`p` is the **zero-based** active-profile index, 0 through 4. User-facing profile
numbers are therefore 1 through 5. For example, profile 1 is
`03 C9 80 00 00`, and profile 5 is `03 C9 80 04 00`.

| Evidence | Exact behavior |
|---|---|
| Formatter `0x31A5A..0x31A70` | Writes `C9 80 RAM[0x40102C] 00` into `0x400B4F`; there is no increment |
| Wired queue branch `0x31A72..0x31A88` | Sets boolean pending flag `RAM[0x400AFA]` |
| USB drain `0x38D24..0x38D40` | Clears the flag, prefixes report ID `03`, copies the four bytes unchanged, calls USB send with length 5 |
| Profile loader `0x30B78..0x30B7E` | Accepts indexes 0..4, otherwise falls back to 0 |
| Profile consistency check `0x31354..0x31368` | Replaces an index at or above 5 with 0 |
| Feature `0x0C` setter `0x341E4..0x341EE` | Converts its one-based setting to RAM by subtracting 1 |
| Feature `0x0C` getter `0x33CAC..0x33CC0` | Adds 1 to the RAM index when constructing its setting reply |

Do not apply the feature reply's one-based convention to the passive packet.
The native vendor HUB parser around `0x41D0A4` and `0x41D64A` validates an
80-command payload as 1..5. That conflicts with the direct stock formatter and
wired USB copy. The new raw parser must follow the proved raw bytes; this audit
does not establish why the vendor parser differs or whether an intermediate
native transport transforms its input.

A strict raw decoder should require exactly five bytes, report ID 03, model C9,
command 80, `p <= 4` and final byte zero. Unknown values must not be clamped or
guessed. This only describes decoding; it does not authorize numeric DPI.

## This is an occasional event, not a reliable profile feed

There is one direct Thumb call to `0x31A5A`, at `0x32A08`. The surrounding
physical profile actions (`0x329DE..0x32A76`) update the RAM index, request
deferred work with code 0C and emit the notification. The emitted index precedes
completion of profile persistence/loading. The deferred worker handles profile
metadata at `0x314A4`, then can schedule its load path at
`0x313F2..0x3141A`; that path calls `0x30B78` and the sensor/apply routine.
The profile event is not an atomic acknowledgement that a table/stage snapshot
already belongs to the newly loaded profile.

The feature 0C setter changes the same active-profile RAM field and schedules
profile work, **without calling the profile formatter**. Startup and ordinary
profile loading also do not establish an initial passive profile notification.
Thus a host cannot infer the initial profile from silence, battery or a DPI
stage event, and another configuration writer can change it without this event.

Profile 80, DPI-stage 10, polling 20 and response-time 70 formatters share the
same four-byte buffer `0x400B4F` and boolean `0x400AFA`. This is a single pending
slot, **not a FIFO**. A later formatter may replace the earlier notification
before USB drains it. The profile event can therefore be lost even when the
transport delivers every submitted USB report correctly. The separate battery
buffer does not fix this shared configuration-event slot.

The two direct calls to the DPI-stage formatter `0x31A0C` are at `0x32590` and
`0x327AC`, in physical DPI actions. The profile loader does not call that
formatter. Do not assume every profile change produces a following fresh stage
event, or that a stage received later proves no unobserved profile change.

## Required cache behavior

A profile notification is useful **invalidation evidence**, but cannot on its
own prove a permanent device/profile/table association:

1. Start every connection with unknown profile, unknown stage and no trusted
   numeric DPI. Do not default to profile 1 or recover identity from disk.
2. On every valid profile 80 event, clear the old stage and numeric join. Record
   the new observed index and a new binding generation. Even a repeated index
   does not prove that its table is unchanged or that no transition was missed.
3. A subsequent stage 10 event can update the labelled DPI stage. Numeric DPI
   additionally requires a verified table tied to the same physical device,
   profile and configuration generation. A stage does not renew table/profile
   trust. A cached disk profile or the vendor HUB's selected UI tab is not that
   proof.
4. Invalidate table/join trust before any profile switch, DPI-table edit or
   configuration transaction; keep it invalid on failure/unknown result. Clear
   it on disconnect, device selection/session replacement, stream failure or
   expiry. Unknown raw input must not extend its age.
5. Because notifications can be absent/overwritten and external writers can
   bypass them, these invalidation rules still do not make passive observation
   alone sufficient. Keep numeric DPI null unless a separate, explicitly
   qualified configuration source establishes the association and its limits.
   Displaying the raw one-based stage remains accurate within its own age.

There is no passive profile epoch, table checksum or full numeric table in
either `03 C9 80 p 00` or `03 C9 10 stage 00`. A polling workaround is not added
by this audit. In particular, report 0A has profile-loading side effects; see
[the readback audit](USB-READBACK.md).

## Reproducible offline proof

Run `tools/emulate_profile_telemetry.py` with the existing WSL analysis
environment and local normalized firmware fixture. It executes the original
Thumb formatters and wired USB assembly, with ROM memcpy/send interception and
explicit wired/endpoint-ready assumptions. It verifies:

- All five exact profile reports, indexes 0..4.
- Profile followed by stage before drain produces only the stage report.
- Stage followed by profile before drain produces only the profile report.
- Two profiles before drain produce only the last profile report.

Result: **5 exact packet cases + 3 overwrite regressions passed**. No firmware
bytes were patched, no device settings changed, and no physical notification
delivery, scheduling latency or complete-change coverage is claimed.
