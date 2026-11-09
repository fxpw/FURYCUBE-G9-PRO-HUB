# OL3 configured-DPI status: protocol and offline proof

Updated: 2026-11-09. **Offline candidate; not installed or physically validated.**
The existing OL2 source/payload/builders are unchanged. OL3 adds a new version
marker and a status response alongside the inherited GO2 page uploader.
The [recovery gate](RECOVERY.md) still blocks firmware installation.

## Exact source and meaning

`ConfiguredDpi` is the unsigned numeric setting used by the stock OLED function
`0x3D328`, read directly from current firmware RAM:

```text
profile = RAM8[0x40102C]                  // zero based, 0..4
stage   = RAM8[0x40104F]                  // one based, 1..8
value   = RAM8[0x40103E + stage]
        | RAM8[0x401046 + stage] << 8
```

There is no host table join, factory default or disk-profile fallback. It is
configured runtime DPI, **not a measurement or independently verified applied
sensor DPI**. Values above 12000 retain the stock OLED's raw numeric meaning.
Invalid indexes and zero value produce an invalid status; no other sensor limit
is invented.

## Capability and host transaction

The same wired configuration handle must first pass normal device/capability
validation: VID `1D57`, PID `2148`, expected interface/descriptor and the existing
8-byte version transaction. OL3 marker:

```text
0B 08 01 01 08 4F 4C 33                 // OL3; logical length 05 also accepted
```

Stock and OL2 do not authorize any status query. Reconnect, handle replacement,
identity change or discovery failure clears capability. The HID descriptor
already declares feature ID **hex0x22**, total 132 bytes including ID.

A status transaction uses the existing strict idle/grant/single-consumer flow:

```text
A0 22 84 00 00 00 00 00                // logical length 132, selector0
```

After ready 1, read GET 22 with exactly132 bytes and consume that reply once.
Only one configuration owner/transaction may be active; status reads and page
writes are serialized on the same handle. The host validates the entire reply,
keeps a receive timestamp and exposes a numeric value for at most 1 second.
Busy/invalid/failure clears it. A previous result is never renewed by an error.

The stock A0 transport **does wake the mouse and reset inactivity state**.
This is explicitly retained, like page uploads. It may initialize/redraw OLED
when asleep. The custom status path introduces no profile load, profile-table
write, persistence or NVM operation.

## Frozen 132-byte response schema

| Offset | Length | Meaning |
|---|---:|---|
|0|1|ID 22|
|1|1|Length 84 hex / 132 decimal|
|2|2|ASCII GS|
|4|1|Protocol 03|
|5|1|Status 0=invalid, 1=valid, 2=busy; all other codes rejected|
|6|1|One-based profile 1..5 when valid; otherwise 0|
|7|1|One-based DPI stage 1..8 when valid; otherwise 0|
|8|2|ConfiguredDpi uint16 little-endian, nonzero when valid; otherwise 0|
|10|120|Reserved, all zero|
|130|2|CRC16/CCITT-FALSE over bytes 0..129, little-endian|

Polynomial 1021 hex, init FFFF hex, no reflection or final XOR. A capability marker
and CRC do not establish boot compatibility, recovery or physical correctness.

## Why two main-context hooks are required

The main loop `0x36404` calls read FSM `0x34216` at `0x3647E`, then configuration
worker `0x3136C` at `0x364D2`. Status production stays in this cooperative path;
it does not snapshot a half-copied main-thread profile through a USB interrupt.
No interrupt-mask veneer or persistent RAM allocation is needed.

1. At `0x342A4`, the prepare hook replaces selector storage/branch. For every
   report 22 read, including malformed selectors/lengths, it stores the selector,
   uses known body pointer `0x400F93` and enters state 3 directly. It skips profile
   preload and preserves both `Update_Profile` at `0x400B27` and macro selector
   `0x400BA2`. Non-22 requests replay the original selector-zero/nonzero branches.
2. At `0x34308`, the GET-data proxy handles every22 request and tail-calls stock
   `0x33B84` for other IDs. It accepts the exact wired request context and the
   original 8-byte A0 query in established OUT staging `0x4011C8`. It clears and
   builds the full 132-byte response at `0x400F91`, without stock GetData wake or
   its active-profile restore. Malformed22 requests produce invalid replies.

The original FSM then sets ready 1 and advances state 3→4. A normal HID GET
consumes ready; state 4 returns to idle 0. There is no new queue/sequence global.
A second GET without a fresh grant returns no response.

Hooking only GetData would have left the selector-zero branch's earlier
`Update_Profile=0` mutation in place. The prepare hook specifically avoids it.
It also prevents malformed report 22 selectors from requesting a stock preload.

## Coherence, pending work and profile-selector history

A valid snapshot requires zero current/queued work at `0x400B2D/0x400B2E`, zero
settings/macro/reset flags at `0x400AA0..0x400AA2`, and no newly pending normal
configuration request at `0x400B14`. A mismatched active profile and
`Update_Profile` returns busy. Profile/stage bounds precede indexed array reads.
Invalid/busy reports have zero profile, stage and numeric fields with valid CRC.

`Update_Profile` is a transport/persistence selector, **not a loaded-profile
epoch**. It is an additional conservative guard. Coherence follows the main
execution path and pending-work checks, supported by original-code histories:

- Original global loader `0x30968` initializes active profile and its selector
  together (`0x30A64..0x30A68`), then the actual profile loader reads that record.
- Normal non-22 reads may temporarily select another profile. Stock GetData
  restores `Update_Profile` from the active index at `0x33CA2..0x33CA6`, executes
  the real profile loader, and only then the FSM sets ready 1. A new transaction
  begins after consumption/idle, not midway through that restore.
- A real profile SET changes active index before deferred loading. It leaves
  pending work, so status returns busy. The original configuration worker then
  persists profile metadata, sets the selector to the active index at
  `0x31410..0x31416`, loads/applies it and clears work. Subsequent status is valid.

All five profiles pass these histories. A selector mismatch remains busy rather
than authorizing a guessed table. Other tools must not run concurrent
configuration transactions; stock's shared ready/response protocol has no
per-client ownership token.

## Retained wake and explicit proof boundaries

The initial A0 SET calls `0x34AF8` before its OUT data arrives. Its audited body
updates sleep/inactivity counters and GPIO/peripheral state. The asleep branch
calls OLED init/render and peripheral wake. It does not call profile load,
configuration apply or a flash writer. `0x3C39C` accesses peripheral MMIO;
`0x39F6C` configures GPIO MMIO.

The emulator executes that wake body and its GPIO/peripheral instructions with
mapped MMIO. OLED init/render are explicit hardware/render boundaries; ROM
unaligned pointer, memcpy/switch and USB FIFO/handshake helpers are modeled.
This is not a complete chip/ROM/display or interrupt-timing emulator.

## Candidate layout and checks

`firmware/g9_ol3.S` includes unchanged `g9_oled.S`. The first 280 compiled bytes
are exactly OL2; its page protocol remains GO2. A separate wrapper marks OL3.

- Own machine code 636 bytes, padded to 640: 20 new32-byte data+CMS blocks.
- Append CPU 0x47400, normalized 0x1EC70, raw resource 0x20B36.
- Original16-byte header unchanged, 3939 original blocks retained except five
  changed blocks for the no-wait fix and four hooks.
- Resource 134622 bytes; aligned updater transfer remains 0x21000 bytes.
- Payload SHA256 `f9938ccff010dd680f1df37c0b0a97a3a6af6c8aeb12859475ca4dc19eba85ed`.
- Candidate resource SHA256 `cf4048c67be8e922bf4a129bfec80c988e448867d57cb44e186a9c01ac7af03a`.

`tools/emulate_ol3_status.py` executes real stock/new instructions.
`tools/build_ol3_candidate.py` reruns those checks against bytes decoded from
the **actual framed candidate**, verifies every CMS block, unchanged blocks,
exact patch offsets, header, padding and aligned limits. Current results:

- 71 whole A0/data-completion/main-FSM/ACK/GET cases: 40 numeric combinations,
 invalid/busy/malformed transport cases, sleep wake, fresh successive reads and
 full status→GO2 page→status interleaving on the same emulated transport.
- 10 original global-load/legacy-restore/profile-SET/worker histories, all profiles.
- 45 non-22 prepare comparisons agree with original RAM/state behavior.
- Four GO2 pages execute native draw instructions;15 malformed inputs are rejected.
- The no-wait zero key passes screen on/off, middle and right-chord cases.
- Stack canaries and callee-saved registers remain intact; status leaves runtime
 configuration unchanged, with no profile reload or NVM routine reached.

History inputs are synthetic flash records. Their persistence boundary is an
explicit stub; no installed firmware was read or written. All candidate files
remain ignored under a unique `patch-output` directory. Own payload HEX is
publishable source; official images and per-device contents are not.

Software integration may continue. Installation and physical validation remain
blocked until independent recovery and a full current backup/equivalent exact
restoration are demonstrated.
