# Numeric DPI: verified settings format and live-source limits

The numerical values in a saved settings frame can be decoded exactly. This
does **not** make that frame a current device readback or prove that a later
passive stage event belongs to the same profile/configuration.

`src/G9DpiSettings.cs` implements only the original HUB's captured outgoing
56-byte `SET 0x04` layout. It issues no device requests. Its immutable result
has `SourceKind = CapturedSet04` and `IsDeviceReadback = false`.

## Verified format

| Bytes | Meaning |
|---|---|
| 0–1 | `04 38`: report ID and 56-byte native outgoing frame length |
| 2 | One-based profile, 1–5; must match the caller's expected profile |
| 5 | Enabled-stage mask |
| 6–7 | Zero in this specific native-encoder variant |
| 8–15 | Low bytes of eight DPI values |
| 16–23 | High bytes of the same eight DPI values |
| 24 | Recorded one-based stage, 1–8 |
| 3–49 | 47-byte payload covered by the checksum |
| 50–51 | Payload sum16, big-endian |
| 52–55 | Zero native transport padding |

Each value is `low[index] | (high[index] << 8)`. The arrays are **not separate
X and Y resolutions**. Disabled entries are preserved; an enabled zero value
is rejected. The parser does not guess another sensor's DPI range or step.

The captured frame contains the values 800, 1600, 3200, 4800, 8000, 12000,
0, 0, with enabled mask `0x3F` and recorded stage 2. The original x86 outgoing
encoder at `0x41E200` reproduces that frame byte-for-byte from the saved native
profile. Its native units map to `unit * 50` through unit 200 and
`(unit - 100) * 100` above that boundary.

Independently, the original ARM OLED routine at `0x3D328` reads the selected
split-byte value and prints it directly as DPI. Sensor application at
`0x2FDCC` converts it later to PAW3311 register settings; it is not a reason to
decode the saved wire values through the shared web lookup table.

The native incoming routine at `0x420560` uses a different representation and
does not invert this outgoing frame. Applying it directly to the captured
bytes produces incorrect values such as 801/1601. The examined driver
`GetFeature` path does not establish a conversion that would justify treating
those formats as interchangeable.

A 52-byte `GET 0x04` response is intentionally **not supported by this parser**.
Its source/transport variant needs separate validation. These findings do not
add a query to the standalone client's allowlist.

## Why the live widget still needs a trustworthy source

The passive stage packet contains no numeric value. Stock profile notifications
can be lost, precede a deferred load, or be absent when another configuration
writer selects a profile. A host must not combine a stored table with a new
stage and silently call that the current DPI. See
[PROFILE-TELEMETRY.md](PROFILE-TELEMETRY.md).

The host now invalidates a cached stage whenever it accepts a profile
notification, including a repeated notification for the same profile. Battery
data remains independent. Out-of-order packets cannot resurrect the invalidated
stage. This reduces stale display; it does not turn the notifications into an
authoritative profile feed.

`TelemetrySnapshot.Dpi` therefore remains unknown: this passive source does not
provide numerical DPI. The separate OL3 candidate now supplies a coherent
runtime snapshot through a capability-gated request. The editor accepts only
a valid, non-busy status no older than one second before rendering an explicit
OLED update. It does not use this saved-table parser to invent a live value.
The source is the configured setting used by native OLED rendering, not a
physical sensor measurement. See [OL3 status](OL3-STATUS-DESIGN.md).

Stock/OL2 sessions cannot read this status. The OL3 candidate and host path are
offline-tested; firmware installation and physical proof remain blocked by
the recovery requirement. No factory defaults or merely remembered settings
are substituted for a missing live value.

## Verification

`tools/build-dpi-tests.cmd` runs 75 offline checks covering the captured frame,
native-unit boundaries, source identity, immutable data, profile mismatch,
checksum, padding, stage limits and malformed frames. Telemetry tests separately
cover profile-triggered invalidation, ordering, reset and expiration. These
checks perform no USB operation and do not prove physical DPI delivery.
