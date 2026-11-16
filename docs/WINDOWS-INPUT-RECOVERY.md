# Windows input recovery with Interception installed

Updated 2026-11-17. This describes a checked recovery on the development PC,
not a prerequisite or an automatic action performed by the HUB.

After DG3 installation, configuration reads and the screen key worked but USB
movement and clicks did not. All 117 saved profile bytes matched. A mouse-
interface restart and a verified single-port cycle did not restore input;
a composite-device restart requested a PC reboot.

The G9 mouse stack included Interception's `mouse.sys`, installed separately
for KeyboardControl. With explicit owner approval, a bounded experiment briefly
removed it from the mouse class registration, cycled only the verified G9 port,
and restored registration after observing the rebuilt stack. An original-value
backup, finally restoration and independent restore watchdog were prepared.

The rebuilt G9 stack contained `mouclass`, `mouhid` and `HidUsb`, without
Interception. The global registration was independently verified as restored to
its original `mouse, mouclass` order. A separate passive USB check then passed
**9/9** and closed automatically; the owner confirmed normal input. **No PC
reboot, additional firmware installation or profile change was needed for this
recovery.** The native DG3 display visibly showed its expected 9000 baseline.

The installed filter binary rejects CONNECT indices of 10 or higher, while this
boot listed `PointerClass0` through `PointerClass15`. The exact G9 class index
was not established: this is a plausible mechanism, not a proven explanation.
Driver registry suffixes and HID PDO names do not prove the class index.
The binary did not implement GET_FILTER; that unsupported query was not sent.

The bypass affects the currently rebuilt G9 stack. Restoring registration does
not insert the filter back into that running stack, but a later rebuild can
attach it again. This was not a permanent filter uninstall. Another mouse
arriving during the brief global registration window could also bypass the
filter. The device-pinned, machine-specific recovery scripts remain private.

Before another firmware installation, inspect the G9 input stack and prepare a
reviewed recovery path. A successful PnP/port-cycle return is not an input test,
and offline EP2 callbacks do not prove physical USB completion. The HUB itself
installs no input filter and does not change class registration automatically.
Radio OLED delivery was not qualified by this wired recovery; the subsequent
radio input check independently passed 9/9.
