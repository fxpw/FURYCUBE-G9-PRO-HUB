# Offline macro editor

`G9MacroEditor` authors a local macro library; it never injects input or sends USB commands. The disabled **В мышь** button explains the existing gate: ordinary keyboard output from button 6 must be confirmed before actual macro upload is attempted.

Create, duplicate or delete a named macro. Each step is an explicit key press/release, one of five mouse-button presses/releases, relative X/Y movement, wheel delta, or millisecond delay. Steps can be edited, inserted, deleted and reordered. Repeat mode/count are saved project metadata; they are not proof of firmware behavior. Missing key releases are shown as authoring warnings.

**Записать в конец…** opens a dedicated recording window. Only keyboard messages delivered to that focused window are recorded. Mouse buttons/wheel are recorded in its blue capture area; movement is optional. Esc or **Завершить** stops recording. Recorded/fixed/no-delay modes are available. OS autorepeat keydown messages and releases for keys already held when recording began are excluded. Stop adds releases for keys/buttons still held, in reverse press order, only to the project. No release is injected into Windows. Input outside the recording window is not monitored.

Import/export uses version-1 `FURYCUBE-G9-MACROS` JSON with symbolic step/repeat names and raw Windows virtual-key codes. It does not read/write the OEM binary `macro.data` format or claim byte-for-byte hardware-packet export. Projects contain no device handles, local paths, sensor readings or credentials. Bounds are 256 named macros per library, 4096 steps per macro, 1–20 characters per name, 1–999 host repeat count, signed-16-bit movements/wheel, and 0–99999 ms per delay. Longer recorded pauses are split into several delay steps without changing their total duration.

Edits autosave after 500 ms to `%LOCALAPPDATA%\FURYCUBE-G9-PRO\editor\macros.autosave.json`; the next editor instance restores that library. Invalid unfinished names are not written over the previous valid snapshot. Explicit JSON export remains available for a portable copy.

Pure validation: `tools\build-macro-tests.cmd` covers an independently authored Ctrl+A/mouse/movement fixture, malformed/foreign schema, bounds, held-key completion, autorepeat timing, long pauses, recording capacity, warning semantics and atomic portable-file replacement.
