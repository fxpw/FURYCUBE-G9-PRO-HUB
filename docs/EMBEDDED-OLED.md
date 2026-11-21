# Embedded OLED snapshot and autonomous battery gauge

This experimental OL9 variant embeds a snapshot of a 120 × 28 layout. It
supports static text, an image and battery gauges. On a cold boot the main
loop seeds the frame store after stock panel initialization. At runtime it
repaints the affected 40-byte chunks when the battery crosses a 10% threshold.
The charge value comes from the same device state as the stock gauge.

The snapshot runs without the HUB. A complete USB or radio upload replaces
the current screen in RAM and switches to ordinary host-rendered pixels.
A warm reset can restore the retained frame. After full RAM loss, the
compiled snapshot is used again.

The screen code performs no runtime flash programming. Saving arbitrary new
layouts across power loss requires a qualified writable region. See [the
retention analysis](OLED-RETENTION-DESIGN.md#full-power-loss-persistence-options).

## Offline build and checks

Compile `tools/ExportEmbeddedScreen.cs` with `src/G9Screen.cs`, referencing
`System.Drawing.dll` and `System.Web.Extensions.dll`. Run the exporter with
the saved layout and a new private output path. It renders eleven reference
frames for 0–100%.

Run from WSL with the checked OEM resources and Python tooling used for OL9.

```sh
tools/pyenv/bin/python tools/emulate_embedded_oled.py /mnt/d/path/to/snapshot.frames
tools/pyenv/bin/python tools/package_embedded_oled.py patch-output/<build>/g9-embedded-oled.OFFLINE.bin
```

The ARM check compares native OLED transactions with the reference frames
for cold startup, charge changes, wake and a complete radio upload. It also
checks that the startup hook preserves the FURYCUBE splash and suppresses
the stock status pixels before the embedded frame appears. The packager
keeps the application sections and other resources byte-identical.
It verifies the relocation bytes and emulates the exact 34-block transfer
within the existing erase bounds. Both build steps leave the updater disabled.

Keep the generated image, updater and personal snapshot local. Independent
recovery from a failed application boot remains unverified.

## Device verification

The first embedded build displayed the saved layout over USB and after
switching the mouse off for ten seconds and back to 2.4 GHz with the HUB
closed. The input check passed all nine actions on both connections. The
stock status screen briefly appeared between the splash and the saved
layout. The startup hook that suppresses this frame passed emulator checks,
and the USB updater completed all 34 blocks. All eight saved profile records
matched after restoring the screen button assignment. The wired input check
passed all nine actions again. After ten seconds in OFF and a switch back to
2.4 GHz with the HUB closed, the user confirmed that the saved text and
battery gauge appeared immediately after the FURYCUBE splash. The battery's
10% transitions were tested in the emulator.
