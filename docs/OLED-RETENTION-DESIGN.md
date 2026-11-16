# Retained screen: isolated storage proof and remaining integration

> Checkpoint: the text below records the isolated storage prototype. The newer
> `firmware/g9_ol5.S` also combines radio transport, pixel ownership and staged
> restoration; `tools/emulate_ol5_retention.py` contains its offline checks.
> That combined source is not installed, has no prepared updater candidate,
> and requires an explicit review of its larger 34-page transfer. The installed
> mouse still uses the 708-byte OL4 extension. Firmware work is paused at the
> owner's request; the earlier design below is not a complete description of
> the combined source.

This is an offline design and executable storage prototype, not an installed
OLED-persistence fix. Existing OL4 source, payloads, updater packages and hardware
are unchanged by this work. The owner wants the HUB to resume on Windows login,
update over supported connections, and leave the last complete frame available
when the HUB is absent. The current physical observation is that the uploaded
picture becomes stock content after USB-to-radio switching.

## Why the current frame disappears

`firmware/g9_ol4.S` validates one packet, snapshots it on the stack and draws its
40 pixels. It retains no image. A screen occupies 480 bytes: 120 columns by 4 pages,
with the upper 4 bits of the final page unused. Native redraw, initialization,
sleep blanking and off are different operations; see [OLED-REDRAW.md](OLED-REDRAW.md).

Connection handling at `0x351A8..0x351AC` requests native redraw. Wake at `0x34AF8`
can initialize the display and redraw native content. Sleep calls`0x3D824`, which
explicitly sends 480 zero bytes. Disabling the dirty dispatcher cannot reconstruct
pixels lost by these paths and does not cover direct native renderers.

Mode transitions can also **reset the MCU**, not merely redraw: for example,
`0x355A8..0x355AE` logs a switch to RF_2_4G_MODE then calls reset `0x3B618`;
`0x35786..0x357A4` is another mode/reset branch. Connection branches also call
reset at `0x34DD0`, `0x350FC`, and`0x35124`. The exact branch taken by the observed
physical cable transition has not been captured. Retention across an ordinary
main-loop redraw must not be advertised as retention across these resets.

## Concrete RAM ownership

The pinned original application has a malloc-like allocator at `0x43004`, free at
`0x42FB0`. Real USB buffer setup uses them at `0x37D7A/0x37D82` and
`0x37DB8/0x37DC2`. The allocator lazily initializes these exact values:

| Item | Original value |
| --- | --- |
| Free-list head | `RAM[0x400D2C]` |
| Initialization flag | `RAM[0x400D30]` |
| Arena start literal | `0x43068`: `0x404F30` |
| Arena end literal | `0x4306C`: `0x406F2C` |

The prototype reduces the last literal by 16 bytes to `0x406F1C`. The reserved
`[0x406F1C,0x406F2C)` is the root record: magic, allocation pointer,
allocation-attempt flag, reserved word. This is an explicit reservation from the
OEM allocator's arena, not an assumption that an unreferenced address is unused.
No global/profile/USB request scratch is borrowed.

An early boot hook replaces exactly`0x28D58: A4 48 00 25` and replays its effects,
`r0=0x40006D`, `r5=0`. It runs after stock data/BSS initialization and before
platform setup. The actual ARM startup finishes in SVC with IRQ and FIQ masked;
the hook performs one malloc(1024) before these setup functions can enable
interrupts. It needs no new interrupt-masking veneer. Allocation is never called
from staging, commit, or rendering. Do not move this allocation into an IRQ or a
live request without separately qualifying the allocator's non-reentrant code.

The first allocation returns`0x404F34` and consumes 1032 bytes, including its
allocator header/alignment. After the 16-byte reservation the remaining effective
free space is 7136 bytes. The arena reservation and allocation have been executed
with original instructions; runtime capacity under every Bluetooth/USB workload
is not physically established. A NULL allocation leaves native mode available.
The reservation requires a fresh boot; changing the arena-end literal while a
live heap exists would not reclaim existing allocations safely.

The stock startup also proves why general SRAM guesses are unsuitable:

| Region | Role |
| --- | --- |
| `0x400A00..0x400D34` | Initialized RW |
| `0x400D34..0x40489C` | Zeroed BSS |
| `0x40D0CC..0x40D12C` | Shared SYS/ABT/UND stack area |
| `0x40D12C..0x40F81C` | SVC stack |
| `0x40F81C..0x40FC0C` | IRQ stack |
| `0x40FC0C..0x410000` | FIQ stack |

The gap above BSS contains this allocator and other data; it is not free simply
because a literal scan finds few references.

## Isolated storage contract

`firmware/g9_screen_store.S` contains three unintegrated helpers:

| Helper | Contract |
| --- | --- |
| `g9_store_boot_hook` | Validate any retained image, allocate1024 once, validate pointer/image again or initialize empty metadata, publish magic last; replay displaced main-entry effects |
| `g9_store_chunk` | `r0=chunk index 0..11`, `r1=validated 40 pixel bytes`; returns0 rejected,1 staged,2 committed |
| `g9_store_active` | Return complete active 480-byte frame, or NULL |

The allocation contains a 4-byte prefix, two 480-byte buffers and 60 trailing
metadata bytes (64 metadata bytes in total). The prefix is deliberate: lazy
stock malloc initializes its free-list next pointer at `0x404F34`, overwriting
the first 4 allocation bytes before returning. Pixel data must start at+4;
otherwise the next boot can corrupt bank0 while rebuilding the allocator.
Metadata currently records active bank, validity, receiving state, next expected
chunk, custom owner, renderer state reservation, restore-pending flag and frame
generation and complete-frame CRC16/CCITT-FALSE. Buffer0/1 exchanges only after
all 12 chunks arrive in strict order.
Chunk 0 begins or restarts a staging transaction. Gaps, out-of-range indexes and
nonzero hidden bits in the last page abort staging without replacing the last
complete frame. Outer transport must validate its packet length/header/CRC first.
Malformed outer packets must never be passed to this helper.

Single main-context ownership is explicit: IRQs may not call these helpers or
modify their allocation. An active-frame pointer remains stable until the next
main-context commit. A synchronous renderer must finish before accepting the
next frame. No claim is made for arbitrary concurrent senders or IRQ writers.

Per complete frame the store copies 480 bytes, calculates the complete-frame
CRC, switches one bank and updates
metadata. It performs **zero malloc/free and zero NVM operations per frame**.
The fixture measures 38176..38285 Thumb instructions per complete store/commit
for its four image patterns. This is an instruction count, not physical latency;
the scheduler/input budget still requires measurement before integration.
The existing transport still performs 12 request transactions; this helper does
not draw, wake, change USB endpoints, or emit input. Existing native raw drawing
can subsequently send four 120-byte pages or 12 existing 40-byte chunks; physical
refresh duration and acceptable scheduler budget still require measurement.

Boot checks the new `GOR2` root magic and exact expected first allocation pointer
`0x404F34`, then bounds-checks bank/valid/owner and verifies the complete selected
frame's CRC before allocation. After malloc it requires the same pointer and
verifies again. A surviving complete image retains its generation/owner, discards
partial staging/renderer state and requests a restore when custom-owned. Invalid
magic, pointer, metadata or image, allocation failure, a different returned
pointer, or lost arena contents produces native fallback. Validation only
dereferences a retained image after the exact known-pointer check.

Both banks survive the **modeled retained-SRAM warm reset**, including actual
ARM startup, BSS reinitialization and the allocator's lazy initialization.
Completely lost/cleared RAM cannot retain an image. The installed loader and
physical reset/power behavior have not been measured, so this is not yet a
physical mode-switch persistence result or full power-loss persistence.

## Next integration steps

1. Establish actual cable/radio transition reset behavior, including whether
   the installed loader preserves this SRAM arena. Model warm resets first.
   The isolated helper now implements retained root, complete-frame CRC and
   deterministic reallocation/pointer matching, with original-instruction warm
   reset tests and corruption/loss fallback. RAM retention through the installed
   loader remains a hardware question; full battery loss cannot use this route.
2. Integrate the validated transport with staging; commit only complete frames.
   Restore the active frame after display initialization/wake in main context.
   A disconnected upload must leave the previous complete frame selected.
3. Establish explicit custom/native ownership and qualify every pixel writer.
   A gate only on`0x3CEE6` is insufficient: stock clear at `0x3D7EC..0x3D824`,
   direct writers around`0x3CE86`, and the`0x3D964..0x3DAB0` family call the
   byte writer`0x3CD7A` directly. Native power-off should remain available;
   wake should restore the complete retained image. A raw low-level gate must
   distinguish commands, addressing/control bytes, pixel bytes and authorized
   custom-renderer traffic; blindly suppressing all I2C bytes is not a policy.
4. Prove native-mode restoration, sleep, settings, DPI, battery, disconnect,
   queued rendering, malformed uploads and both input endpoints before a
   combined candidate is generated. Keep the sixth-button fix and EP2-ready
   guard unchanged unless new evidence requires a scoped change.

The current 528-byte helper and 708-byte OL4 payload total 1236 bytes before any
integration glue, requiring 39 CRC blocks and a 135268-byte resource, 100 bytes
above the current 33-page transfer. Current builders reject an expansion beyond
`0x21000` bytes. Reusing the existing OL4 CRC helper could reduce duplication,
but RF and native-ownership glue still need a fresh size/transfer-bound review.
No candidate with an expanded transfer has been generated by this prototype.

## Full power-loss persistence options

No dedicated writable picture sector is currently qualified. Do not use
`0x52000` (macro/OAD scratch), `0x7C000` (settings), `0x7D000` (macros/update
marker), or apparently empty profile/pairing space. Generic ROM flash users and
the installed loader prevent treating an absent direct reference as ownership.
The known macro setter's scratch is an old bank, not a current backup; see
[MACRO-STORAGE-PRESERVATION.md](MACRO-STORAGE-PRESERVATION.md).

An immutable image embedded in a reviewed firmware resource can survive actual
power loss without new runtime flash writes, but replacing it requires another
firmware update. Even 708+480 raw bytes round to 38 additional CRC blocks and
exceed the current 33-page transfer by 66 bytes, before new restore code. Simple
images may compress; arbitrary pictures need a newly reviewed resource budget.
This is a limited fallback, not the desired frequently editable widget storage.

For writable persistence, first qualify a dedicated erase region and full
preservation of OEM nonvolatile data. Then use versioned records with image
dimensions, generation, CRC, last-good recovery, readback and interruption tests.
Use explicit **Save to mouse**, never a flash erase/write every widget frame or
every second. PC-dependent widgets freeze at their last transmitted values while
the HUB is absent; autonomous changing widgets require separate device code.

Windows autostart and loading saved layout/settings are host features and can
restore/retransmit after reconnect. They cannot themselves preserve an image
while no host is running or while the radio path cannot deliver a frame.

## Reproduction and limits

Run from WSL in the repository:

```sh
PYTHONDONTWRITEBYTECODE=1 tools/pyenv/bin/python tools/emulate_screen_store.py
```

The tool compiles into a temporary directory and runs original ARM reset,
banked stack setup, RW copy/BSS zero, modified-entry replay and actual stock
malloc/free with no ROM/MMIO/startup stubs. It tests complete/partial/malformed
frame sequences, bank swaps, simulated USB allocations/free, OOM and native
fallback, bounded RAM writes, both warm-reset banks, seven metadata/pixel
corruptions, lost arena, deterministic-pointer mismatch and cold-zero RAM. It
compares original versus hooked startup registers and complete CPSR, including
flags and interrupt masks. It writes no firmware artifact and accesses no
hardware. Current helper code is 528 bytes, SHA256
`57e98053ff310eb404d9ac67beeca0777bf0515282da97c8b11e7c2d5cfbad0d`.

Pinned original resource SHA256:
`f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc`.
The test has not demonstrated physical SRAM retention, actual OLED ownership,
radio delivery, loader preservation, flash persistence, or long-term stability.
