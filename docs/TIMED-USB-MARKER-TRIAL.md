# Timed USB marker: conditional trial mechanism in the SDK source

2026-11-09. Offline source audit and original-C host fixtures with mocked
flash, protection, USB, watchdog and image execution. No device was opened,
reset or modified. No marker-writing command, transport or live procedure is
provided. This is the pinned SDK mirror, **not the installed G9 BIM**.

**New positive finding:** if a persistent `1234` marker already has byte 2 set
to `55`, this source can start USB, time out into its executable image without
removing the marker, and start USB again after a reset. This is a concrete
conditional mechanism for an app-only trial. It is not the stock update flow,
and its required marker provisioning, usable USB window, reset and G9
compatibility are unproven.

The source is [BK3633_DesignKit_V06_220F at commit
7720ca89ae8dbe26bc37942a33c675c97f127509](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/tree/7720ca89ae8dbe26bc37942a33c675c97f127509).
Related identity/read16 limits are in [USB-RECOVERY-SOURCE-LEADS.md](USB-RECOVERY-SOURCE-LEADS.md).

## Marker lifetime and the loop

`bim_main` reads the marker once. An existing signature selects USB before
examining the image header. Byte 2=`55` permits exit using the 24-bit
little-endian threshold in bytes 3..5; other signed markers have no such
timeout. Exit does not clear flash or the RAM signature. Debug-OUT reception
resets `check_cnt`; ordinary idle iterations increment it. The threshold is
**loop iterations, not a calibrated time**. `STAY_ROM_CMD` sets a variable that
this loop never tests, so one such command does not pin a timed session.
[Main and command dispatch](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_app.c#L478).

The reset source copies initialized data, zeros BSS and enters `bim_main`,
restarting `check_cnt` from its source initializer. This supports a fresh window
after **a reset that reaches this reset vector**; it does not prove that an app
crash automatically resets, or that removing USB resets a battery-powered G9.
[Reset implementation, lines 233–255](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_handlers.s#L233).

## Selector operations preserve this marker, not all NVM

The erased-backup branch simply selects the executable image. Normal/abnormal
APP and STACK backup paths can erase or copy other image/backup regions, but
their inspected cleanup ranges end at **`0x7C000`, exclusive**, below the
marker at **`0x7D000`**. That preserves this marker in the tested source paths;
it does not make those paths read-only or prove preservation of all other data.
[Selector and erase helpers](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_updataImage.c#L373).

Startup/entry still invokes flash-protection helpers. The positive result is
about marker-array contents, not unchanged protection/security state.

This distinction also matters for scratch storage at **`0x52000`**. The source
reads that address as an APP backup header. A synthetic non-erased APP-labelled
header classified as abnormal reaches backup cleanup: **the timed marker
survives, but scratch storage is erased**. The fixture executes the original
header reader, classifier and selector for this case. Thus preserving the marker
cannot establish preservation of an old macro-bank copy or other staged data
at that address. Whether the installed G9 BIM classifies the actual macro-store
scratch this way remains unverified.
[Backup-header interpretation](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_updataImage.c#L38).

## Why the current stock workflow does not provide this trial

Command **`0x10`** explicitly erases **`[0x7D000,0x7E000)`** before calculating
its range CRC and requesting reset. The G9 OEM host always includes its final
range-check exchange. In this mirrored implementation that removes the
retained marker: the next reset, with otherwise valid image metadata, need not
start USB. Stock source entry writers instead create byte 2=`AA`; no inspected
writer provisions the timed `55` state.
[Verification handler](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/boot_usb/app/bim_app.c#L80),
[example application entry writer](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/app_usb_2.4g/app/src/main.c#L154),
[exact OEM host flow](G9-UPDATER-USB-PROTOCOL.md).

There is also an application-side persistence condition: the sample pairing
path writes at `0x7D210` through `flash_write_some_data`. That helper reads
1 KiB, erases the containing 4 KiB sector, then rewrites 1 KiB. A completed
operation restores the copied marker; interruption between erase and rewrite
does not. This defeats an unconditional marker-retention promise across
arbitrary application behavior.
[Pairing caller](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/projects/app_2.4g/app_usb_2.4g/app/src/2.4/application_mode_page.c#L205),
[read/erase/rewrite helper](https://github.com/himaexternal/BK3633_DesignKit_V06_220F/blob/7720ca89ae8dbe26bc37942a33c675c97f127509/src/driver/flash/flash.c#L667).

## Reproducible offline evidence

Public reproducible source audit:

[`tools/audit_timed_usb_marker.py`](../tools/audit_timed_usb_marker.py), using
the already cached, hash-pinned source files and the existing host C compiler:

```text
wsl -e /mnt/d/repos/fxpw/test/FURYCUBE-G9-PRO/tools/pyenv/bin/python tools/audit_timed_usb_marker.py
```

Its ignored local result is
`evidence/recovery-source-leads/timed-marker-results.json`, SHA256
`026479cecade1b444f0c8001d9308a4e4f7b377bfc21525b90c985d94b8154ec`.

The fixture compiles original `bim_main`, command dispatch, selector and
erase/copy helper bodies. Source hashes are pinned. Hardware functions are
memory-only mocks; DATA/BSS reset and failed image execution are simulated.
The final executable-image target is replaced by a test stop, not an ARM app.
The non-timed `AA` case stops at the mocked USB boundary rather than executing
an infinite physical wait. Sanitizers were enabled.

**Thirteen compiled-original-C scenarios passed:** timed handoff; subsequent
reset with the marker retained; a one-shot STAY_ROM flag that still times out;
six APP/STACK selector cases; an actual source classification that erases
scratch while retaining the marker; verification erasing the marker; the
resulting reset without USB; and stock `AA` entry. **Two traffic-loop models** check
idle exit versus repeated received-packet resets. **Two sector-write models**
distinguish completed pairing rewrite from interruption after erase.

No fixture establishes physical enumeration timing, reset reachability,
flash power-loss behavior or compatibility with the user's installed loader.

## What would make this useful for a qualified G9 trial

The next necessary evidence is exact installed-loader behavior matching this
marker branch; a safe way to preserve/provision its state without losing the
original macro/NVM contents; an independently reachable reset and a measured
usable USB window; and preservation of that state through the tested app and
restore workflow. An ordinary IN poll, a command named “stay ROM”, or a valid
app header alone supplies none of these guarantees.

The finding narrows a possible app-only recovery design without requiring an
immutable-ROM bypass of a damaged BIM. It does **not** clear the current G9
flash gate, authorize a new marker writer, or recommend skipping the vendor's
verification and trying a patch on the working mouse.
