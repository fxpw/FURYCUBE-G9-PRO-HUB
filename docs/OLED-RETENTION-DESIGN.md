# Retained screen: isolated storage proof and remaining integration

> Checkpoint: the text below records the isolated storage prototype. The newer
> `firmware/g9_ol5.S` also combines radio transport, pixel ownership and staged
> restoration; `tools/emulate_ol5_retention.py` contains its offline checks.
> A raw BIN candidate passes offline
> checks; the separately bounded 34-page updater package also passed the host
> checks described below. OL5 subsequently completed a 34/34-page owner-authorized
> USB installation and returned the expected version marker. Ordinary input and
> one USB image are physically confirmed; radio delivery and retention are still
> being checked. Work resumed after localization;
> the earlier design below is not a complete description of the combined source.

## Combined OL5 checkpoint, 2026-11-16

`tools/build_ol5_candidate.py` now produces a separate pinned raw candidate:
1924 bytes of ARM payload, 61 appended CRC blocks, 136016 bytes total and 4000
validated CRC blocks. Raw BIN SHA256 is
`f5488618c511e1fab2cd1253fc69546b8d8b6aeaff76d58982e3cf8497d2f97b`.
The original image changes in exactly 28 CRC blocks. OL4 sources, payload,
builder and its 33-page bound remain intact. The physical checkpoints below
record the later installation separately from these offline artifact checks.

The combined regression runs against the bytes decoded from that exact BIN:
568 radio cases, 800 wired IRQ cases, 53 endpoint-readiness cases, 195 native
pixel-gate comparisons, 21 preserved command callsites, 23 malformed uploads,
12 bounded render ticks, and real stock warm-reset/startup. Four additional
cases cover sleep just before final commit, with/without a previous initialized
flag and with/without AF1 suppressing stock initialization. Commit now invalidates
the pre-sleep initialization state and checks whether the real final-AF hook ran
before requesting initialization. No per-frame nonvolatile writes are added.

`tools/emulate_updater_transfer.py` independently runs pinned original x86 host
instructions using synthetic payloads and local HID/OS test doubles. Original
33 pages erase `[0x2B000,0x51000)`; stock 34 pages erase
`[0x2B000,0x52000)` while programming only `[0x2B00A,0x4D00A)`.
The additional 4-KiB sector is not backed up or qualified. An offline six-byte
proposal at `0x4033B9` uses the original 33-page **erase length only**; the actual
34-page programming length is reloaded at `0x4036C5`. All program bytes fit
inside the original erase range. Eight normal/error cases pass for each of the
three configurations (24 modeled runs). The bounded proposal was later packaged
and installed as described below. The host's weak reply checks remain; a success state is not
independent flash readback.

There is a separate packaging issue: static instructions at
`0x404C90..0x404E58` obtain SizeofResource, round it to 4 KiB, then pass that
rounded length to memcpy at `0x404E45`. For this candidate the excess is 3248
bytes. The preceding destination memset does not prevent reading beyond the
resource. Resource loading, padding and this copy required the separate executable
proof/fix below. The transfer-only emulator starts with
a supplied context/buffer and does not claim to cover resource loading.

Only the active profile 1 has a fresh complete USB logical-settings backup at
1600 DPI/1000 Hz. The client rejects inactive-profile reads until explicitly
selected; no profile switch was performed. This is neither a full firmware dump
nor a macro-body archive. Receiver relay, physical SRAM retention, long-running
input stability and hard-power-loss behavior remain separate checks.

### Bounded updater package

The subsequent packaging step uses `tools/ol5_updater_package.py` and
`tools/emulate_updater_prepare.py`. The original preparation function's rounded
copy was executed and reproduced all 3248 excess bytes. The corrected exact
16-byte span at `0x404E3D` copies 136016 bytes, using the destination returned by
the preceding memset. The remaining 3248 transfer bytes stay FF. The prepared
139264-byte buffer is then passed through the same complete x86 update workflow;
all 34 program commands reproduce it exactly, and erases end at `0x51000`.
OS/resource/CRT and HID hardware are explicit emulator boundaries.

The separately packaged disabled EXE has SHA256
`8bc859cf1e221f8181ab0e8d98ab5f6afa96a8ec588dd6e3744e58e9db07fa29`.
All 748 other resources are preserved. Its file length, section raw offsets,
mapped pages, all other section contents and unrelated header fields remain
original. Resource packing fits the original raw section, but needs its
VirtualSize and ResourceDirectory.Size increased by 256 bytes. Those two fields
and the two explicit code spans (6 and 16 bytes) are independently checked;
this package does **not** claim resource-only changes or wholly identical headers.
OL4's previous resource-only verifier and 33-page bound remain unchanged.

Reproduce with the private official input and existing Python environment:

```sh
tools/pyenv/bin/python tools/build_ol5_candidate.py
tools/pyenv/bin/python tools/emulate_updater_transfer.py
# On Windows, with pefile installed:
python tools/ol5_updater_package.py PATH_TO_OL5_CANDIDATE_BIN
# WSL: verify the actual disabled EXE and its exact prepared transfer:
tools/pyenv/bin/python tools/emulate_updater_prepare.py PATH_TO_OL5_CANDIDATE_BIN --package PATH_TO_DISABLED_EXE
```

These tools do not launch the updater. Installation is a separate owner-authorized
experiment. Host completion is not independent byte-for-byte flash readback;
the inherited weak reply checks and unverified emergency recovery still apply.

### Physical installation checkpoint

The owner-authorized USB experiment completed 34/34 program pages and returned
to PID2148. A fresh version response was `0b080101084f4c35` (OL5). No second
update attempt or OLED upload was sent. The OEM update process reset the screen
button binding from `11 00 27` to `0D 00 00`; the saved ordinary key binding was
restored with a verified narrow assignment. A subsequent full USB logical-profile
read matched all 117 pre-update bytes, including 1600 DPI and 1000 Hz. This
settings comparison does not cover firmware, inactive profiles or macro bodies.
The owner confirmed movement, left/right clicks and the assigned key, but reported
a missing wheel click. Raw Input recorded 15 middle-button down and 15 up events
from G9. The initial diagnostic's wheel counter measured scrolling only; its
replacement displays middle down/up and ordinary Windows MouseDown separately.
The owner subsequently confirmed the wheel click works. The updated probe
recorded six middle down/up pairs and six ordinary Windows MouseDown events
inside its window. No device configuration or firmware changed during that
diagnostic correction. A single USB control frame was then submitted as 12 short
reports without error, with the HUB closed. The owner confirmed the visible
USB image and continued movement/left/right/middle input. After unplugging USB
and selecting 2.4 GHz, the picture reverted to stock while input still worked.
The power-switch position during that transition was not recorded; this does
not yet distinguish failed warm retention from actual power loss.

The first radio image attempt discovered OL5, but its first64-byte SetFeature
failed with Windows error87 and no successful chunk. The Windows HIDAPI code
pads short feature buffers to the device capacity. `G9Wire.OledSetBuffer` now
preserves the validated64-byte GO4 prefix/CRC and zero-pads only receiver
transfers to134 bytes. Wired writes stay64 bytes to preserve the EP0 assembler
limit. Eight new protocol fixtures cover sizes, unchanged prefix, zero tail,
caller isolation and rejects;69 protocol and280 settings-transaction checks pass.
See the [primary HIDAPI implementation](https://github.com/libusb/hidapi/blob/master/windows/hid.c#L1230-L1265).
The new envelope submitted12/12 chunks of RADIO OK/G9 PRO without a host error,
but the owner confirmed no picture while ordinary input still worked. The next
one-shot `oled-status` stopped during its initial ordinary GET0B discovery:
A0 stayed idle until timeout. It never submitted a custom GET22 request. Thus
neither relay delivery nor a GET22 length issue is established by that attempt.
No further receiver feature queries, images, resets or firmware writes followed.

The native client now rejects receiver OLED **before discovery or any feature
I/O**, independently of the mouse-side OL5 marker. Vue disables radio upload/live
and explains the USB requirement in RU/EN; its editor remains usable. Normal
radio configuration/version operations are outside this OLED-only guard. The
updated native protocol suite has72 checks;280 transaction checks still pass.
The actual CLI rejection was verified to produce no GET/SEND commands. After
the owner connected the cable, wired OL5 version and complete profile reads
succeeded. All117 logical-profile bytes still match the pre-installation backup,
including1600 DPI/1000 Hz and assignments. No new firmware/configuration write
was required for that check. This is a working wired channel, not proof that
the receiver state or forwarding defect is fixed. The subsequent controlled
test held the switch at 2.4 GHz before and after unplugging only USB: the owner
confirmed the wired image, then stock content without USB, with input working.
This excludes deliberately switching OFF but does not identify an electrical
reset, the exact firmware reset branch, or whether SRAM was preserved.
The SDK source lead is not an exact G9 receiver configuration implementation;
no cause is claimed from that unrelated sample. No OL5 live session has run.

The remaining design notes describe the earlier offline storage prototype.
The later OL5 installation above has not yet established physical OLED retention.
Existing OL4 source, payloads and updater builders remain unchanged. The owner
wants the HUB to resume on Windows login,
update over supported connections, and leave the last complete frame available
when the HUB is absent. The current physical observation is that the uploaded
picture becomes stock content after USB-to-radio switching.

### OL6 carrier experiment and revised priority

The owner prioritizes retransmitting the last complete frame from the running
HUB after a connection change. First qualify radio delivery, then host reconnect
detection/resend. Standalone retention through resets remains a separate goal.

`firmware/g9_ol6.S` and `tools/emulate_ol6_carrier.py` implement a reserved
56-byte SET04 envelope through the stock mouse-side RF reassembler (30+24 body
bytes). A first prototype wrongly assumed sensor fields6/7 were reserved zero;
the captured successful DPI command disproved this before any installation.
The installed version instead uses invalid native DPI stage0 at byte24 and a
revision tag in byte55 (zero padding in normal56-byte settings). It validates
header, CRC, bounds and active profile1, then stages ordinary GO4 pixels.
Pixels occupy12..23 and25..52; CRC16 covers0..52 and is stored LE at53..54;
the revision tag is6. Normal captured-format SET04 retains the dispatcher and
entry arguments. Direct report22 RF requests are discarded. Actual receiver
framing/delivery remains unqualified. Never submit this envelope to stock/OL5.

`G9Device ol6-radio-frame` is a separate experimental command. It requires a
fresh OL6 version and active profile1 on the same open FA60/134 interface,
sends twelve exact56-byte reports with500ms pacing and stops on the first error.
It does not retry, select another profile, issue GET22 or run a follow-up query.
The generic settings allowlist rejects carrier packets. Ordinary HUB radio OLED
remains blocked until physical delivery is qualified.

The offline run passes full 12-chunk assembly, 12 native render ticks, preservation
of the previous complete frame during a partial replacement, 34 corrupt or
semantically invalid packets, unchanged logical configuration and zero custom
flash writes. It also checks the ordinary key paths and 53 mouse endpoint
readiness cases and800 wired IRQ cases. The complete wired/native pixel, sleep,
initialization and retained-reset regressions also run against the exact decoded
candidate. Normal sensor SET is an explicit dispatch boundary, not an
emulation of its complete flash semantics. RF FIFO/transmit hardware and timing
are stubbed. Invalid nonzero legacy prefix selectors are not qualified by this
prototype; mouse validation requires zero kind and active profile1. Physical
receiver-generated prefix fields still need qualification.

Payload:2012 bytes, SHA256
`1c7bd6890516b1257fc7c9189a03576926718d603cabfaa6b7cea5153a0ffc01`.
The separate pinned resource is136084 bytes (63 appended CRC blocks), SHA256
`573c0f087f89f1e86cc2cfefc71ba8aafd73327fefb4a6819d3d539aca54c710`.
The bounded updater has SHA256
`4e271c3b171fd65dc79174f2de39964ebc6d44699c66fb82d4f4531384192645`.
Exact PE preparation/transfer emulation verifies748 other resources, no memcpy
overread,34 program blocks and the original erase end0x51000. OL5/OL4 package
pins and limits remain unchanged. Reproduce offline with private source evidence:

```sh
tools/pyenv/bin/python tools/emulate_ol6_carrier.py
tools/pyenv/bin/python tools/build_ol6_candidate.py
# Windows: tools/ol6_updater_package.py CANDIDATE.bin
# WSL: tools/emulate_ol6_updater.py CANDIDATE.bin PACKAGE.exe.disabled
```

The authorized installation completed34/34. USB returned version
`0b080101084f4c36`; after restoring the ordinary screen-key assignment, all117
profile bytes matched the fresh pre-install backup (1600DPI/1000Hz/profile1).
The owner reports working radio input but missing input when USB is attached.
USB configuration reads still work and PnP reports no interface errors. No OL6
image has been sent by either transport; input diagnosis precedes display tests.

### OL7 configuration/input regression

The owner narrowed the OL6 symptom: USB key0 works, but movement/clicks do not;
radio input works. A successful restart of only the G9 USB mouse interface did
not restore movement. Native-instruction replay then reproduced a separate bug:
keymap SET08 marks a busy EP2 ready and writes into its busy FIFO. A0 discovery
already uses a different callback path and does not reproduce this conflict.

OL7 extends the same readiness guard to SET04/05/06/08/0C; other report IDs and
the real EP2 completion callback remain unchanged. New regression coverage has
47 settings/input cases, including independent EP1 keyboard output, alongside
the complete800 IRQ/53 endpoint/native drawing and carrier suites. This proves
the modeled conflict and correction, not the complete physical failure cause.

OL7 completed34/34 update blocks and returned version`0b080101084f4c37`.
Before any settings write the owner reported briefly seeing movement, then no
working cursor/clicks; the passive trace at this checkpoint contains only the
FA60 mouse source, so it does not confirm USB movement. After restoring key0
and1600DPI, all117 profile bytes match the pre-update backup. No OL6/OL7 frame
has been uploaded. The owner subsequently restarted Windows and confirmed
working USB input. A brief screen change followed by the stock display at
connection remains unclassified; it does not prove a successful host upload.

The next bounded radio session stopped before any image: ordinary GET0C stayed
A0-idle until timeout. A targeted receiver restart returned Windows code 3010
(restart required), not a completed reset. No image, follow-up drain or retry
was submitted after that failure. The original RF path has been audited against
the pinned stock instructions; see [OL7-RF-LEGACY-AUDIT.md](OL7-RF-LEGACY-AUDIT.md).
The separately built OL8/OL9 hypotheses remain uninstalled; their bounds, hashes
and next physical checks are in [RF-CANDIDATE-MATRIX.md](RF-CANDIDATE-MATRIX.md).

The host now saves its last successful complete submission in an atomic,
checksummed PC cache. Reconnect replays the exact static frame or resumes live
rendering; startup requires the existing resume preference. Failed or stale
submissions cannot replace the saved frame, and corrupt cache files suppress
automatic restore. These cases have offline coverage; physical reconnect
replay remains pending, and this cache does not prove device-side retention.

Payload2032 SHA256:`1c7da04f67a9d40346d2fcf9dc34b2490ef6a41cea47c8837485ce8b98b58290`.
Raw136120 SHA256:`bdb11da81885db47f7498f63643c1750e685eeca701e6054266ff4787e894f24`.
Updater SHA256:`44bfc10c87433ea8b5de2da7273d6e7e8d6bd2a3d82c7fd8ae286370da1683b8`.
The container has4003 CRC blocks plus twoFF alignment bytes. Exact updater
preparation/transfer retains748 other resources and the previous erase bound.

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
