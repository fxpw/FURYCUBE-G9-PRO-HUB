# G9 PRO OEM settings parity inventory

Offline inventory, 2026-11-09. The scope is the supplied **G9 PRO / C9 / ms_1** page, not every feature found in a shared multi-model HUB. No vendor executable was launched and no device command was sent for this audit. New-HUB status below is a source snapshot before the settings/profile/macro integration in progress.

## Evidence and interpretation

- `portable-hub/res/main.xml` selects G9 PRO as device 1 and includes `MS/MS_1/ms_1_page.xml`; native model C9 routes to this page at `FURYCUBE.exe` VA `0x40DE11`.
- `portable-hub/res/MS/MS_1/ms_1_page.xml` is the layout; `portable-hub/res/lan/RU.xml` supplies the Russian labels. These local OEM files are intentionally excluded from the public repository.
- Native references below refer to the offline disassembly `evidence/furycube.asm`. Address evidence distinguishes an actual handler from an unused shared resource string.
- A hidden **content panel** under a visible collapsible header is an available collapsed section. A header explicitly `visible="false"` is not an exposed G9 setting. A generic handler or a nonzero saved field alone does not prove that hidden feature is supported by this hardware.
- Installed `Data/ms_1/pro.data` is a saved host library, **not a fresh mouse readback and not factory defaults**. Its named reference record starts at `0x867F8`, size `0x1AE64`. The first five fixed-size records are blank; their presence does not establish a five-item host-library limit.

## Exposed G9 controls

| Setting | OEM G9 behavior and bounds | New HUB at inventory time | Evidence |
|---|---|---|---|
| Physical button assignments | Six visible buttons: left, right, middle, forward, back, OLED/button 6. Full old action catalogue, including key/combinations, mouse, media, DPI and existing macro selection. Extra generic physical button slots are hidden. | Six-button editor and old action catalogue implemented; writing an ordinary key remains subject to the unresolved hardware-output issue. | `ms_1_page.xml:9–85`; native action decoder/serializer; `docs/BUTTON-ACTIONS.md` if present. |
| DPI stages | Six visible editable stages, current-stage selection and Apply. Stages 7/8 hidden. No per-stage enable checkbox exposed. Separate X/Y arrays exist in the native object, but no independent X/Y editor is exposed on this page. | Decoder exists; full settings editor/write integration in progress. | `ms_1_page.xml:174–289`; native refresh `0x418FEF–0x419123`; apply `0x41E200`. |
| DPI numeric bounds | Supplied G9 page and numeric-entry handler expose **50–22000**: 50-unit steps through 10000, then 100. Internal slider units 1–320; `u<=200 => u*50`, otherwise `(u-100)*100`. This proves the OEM UI bound, **not physical PAW3311 support above 12000**. Configured sample reaches 12000. A conservative 12000 cap in new UI must be described as a hardware-validation restriction. | Do not silently substitute 26000/3395 limits or infer actual DPI from profile notifications. | XML slider/max label `:190–191`; native numeric clamp `0x41B5D9/0x41B5E1`, minimum `0x41B648`; display `0x41904A–0x419059`. |
| Polling rate | Visible section with 125/250/500/1000 Hz. Shared alternate panel also lists 2000/4000/8000, but runtime connection/model flags choose panel visibility; this audit does **not** establish G9 support for those higher rates. Preserve the standard G9 range unless independently proven. | Missing dedicated setting at inventory time. | `ms_1_page.xml:330–369`; refresh profile+`0x938` at `0x4191BA`; visibility helper `0x41F8B0–0x41F9C2`. |
| Power: level-1 sleep | Russian label “Время до перехода в спящий режим 1 уровня”. Slider 1–60, displayed **0.5–30 minutes in 0.5-minute steps**. The control name `slider_light_off` is misleading; use displayed semantics. | Missing dedicated setting at inventory time. | XML `:381–390`; profile+`0x930`; helper `0x419A10` multiplies by float `0.5` at `0x419B53` and formats `%.1f` with `sleep_timer_2_text` (“Минут”). |
| Power: standby timer | Russian label “Таймер режима ожидания”. Slider 1–60, displayed **1–60 minutes**. Do **not** name this `idleSeconds`: native helper actually uses the Minutes resource. | Missing dedicated setting at inventory time. | XML `:393–402`; profile+`0x92C`; helper `0x419890`, integer raw value at `0x419990`, unit resource at `0x419952`. |
| Button debounce | Russian “Время отклика кнопок”. **4–50 ms, step 2 ms**; slider native units 2–25, display twice the stored value. | Missing dedicated setting at inventory time. | XML `:419–425`; profile+`0x95C`; refresh `0x4192F9`, edit `0x41B305–0x41B341`. |
| Ripple control | Visible Off/On section, Russian “Контроль пульсаций”. | Missing dedicated setting at inventory time. | XML `:428–434`; native option literal `0x4736F8` at `0x41892E+`; object+`0x3E4`; profile+`0x944`, handler `0x41AD92–0x41ADC4`. |
| Angle snapping | Visible Off/On section, Russian “Коррекция углов”. | Missing dedicated setting at inventory time. | XML `:436–442`; literal `0x473734`; object+`0x3EC`; profile+`0x948`, handler `0x41ADD2–0x41AE04`. |
| Windows mouse properties | Opens the Windows mouse-properties dialog. It is a shortcut, not an OEM implementation of pointer acceleration/speed. | Missing shortcut at inventory time. | XML visible mouse-properties header/button preceding power section. |
| Profile library | Visible list; add, delete, rename, import, export; profile-name dialog allows 30 characters. Visible main Reset button. Do not conflate this host library with the firmware's five onboard profiles. | Device editor was restricted to profile 1; host-library CRUD/import/export not yet implemented. | `ms_1_page.xml:94–145`; `profilename.xml`; native saved-record stride `0x1AE64`. |
| Macro manager | Visible add/delete/name/import/export, keyboard recording, five mouse buttons, mouse movement insertion, step editing, delay editing, reorder, delete/clear. Macro name limit 20 characters. Delay modes: recorded intervals, fixed default (XML starts at 10 ms), minimum delay. | New offline editor/model implemented in `G9Macro.cs` / `G9MacroEditor.cs`; integration pending. Portable JSON is intentionally separate from undocumented OEM binary import/export. Actual device upload stays gated until ordinary key output is confirmed. | `ms_1_macropage.xml`, especially `:60`, `:92–105`, `:115–200`. |
| Macro repeat settings | Count field starts at 1, three numeric characters. Modes: counted repeats, repeat until a key press (OEM RU label), hold to repeat. This XML limit alone does not prove every value 0–999 is accepted by native validation. | Offline parameter editor implemented, with host-project count 1–999. Device encoding/upload not claimed. | `selectmacro.xml:14`; RU `select_macro_3_text` through `select_macro_6_text`. |
| Battery | Visible status indicator, not an editable parameter. | Passive battery telemetry/widget implemented; absence/expiry displayed honestly. | `ms_1_page.xml:124+`; `src/G9Telemetry.cs`. |
| HUB language | Visible Simplified Chinese, English and Russian. Other locales in the shared list are hidden. | New HUB currently Russian. | `main.xml:279–291`. |

## Shared-resource controls not established as exposed G9 features

| Generic feature | What is actually present | Parity decision |
|---|---|---|
| RGB lighting | Main lighting header and container hidden. Shared controls have mode, brightness, speed and RGB dialogs. DPI colour buttons 5/6 lack an explicit hide flag, unlike 1–4: preserve colour metadata, but this quirk does not establish G9 RGB hardware. | Do not advertise 12 lighting modes as confirmed G9 support. Preserve untouched bytes on narrow edits. |
| Lift-off distance | Header hidden. Generic 1.0/2.0 mm choices; extra 0.7 mm choice hidden. | Not a missing exposed G9 control; no PAW3395 assumptions. |
| Motion Sync | Header hidden, although native field/profile+`0x94C` and handler exist. | A saved On value is not proof of capability. Preserve unknown/hidden configuration. |
| Wake by movement | Checkbox explicitly hidden. | No confirmed G9-facing control. |
| Lighting hotkey (buttons 2+4) | Header explicitly hidden. | No confirmed G9-facing control. |
| Gun/recoil manager | Generic button hidden. | Exclude from claimed G9 parity. |
| Autostart, skins, About/menu | Controls explicitly hidden in supplied main layout. | Not a missing exposed setting. |

The bounded native visibility audit found runtime toggling for polling/ripple and connection-dependent sections (`0x41F8B0`). It did not establish a C9-specific enabling path for the hidden LOD/Motion Sync/lighting headers. This is an exposure finding, not an absolute claim that their firmware fields can never have an effect.

## Saved reference values, not defaults

The installed and portable saved record named `Профиль1` contains DPI units `[16,32,64,96,160,220,0,0]` at native offsets `0x8C4` and `0x8E4`, giving `[800,1600,3200,4800,8000,12000,0,0]`. Enable entries at `0x904` are six ones then two zeroes; selected-stage index at `0x934` is 1 (second stage). Polling index `0x938` is 3, corresponding to 1000 Hz in the standard panel. Standby `0x92C=10` displays 10 minutes; level-1 `0x930=20` also displays 10 minutes. Debounce `0x95C=4` displays 8 ms. Ripple `0x944=1` and angle `0x948=1` select On. Hidden Motion Sync has `0x94C=1`; this is deliberately not promoted to a supported feature.

There is no verified factory-default snapshot in this inventory. Never populate a new device read failure with these saved host values and present it as current device state.

## Implementation boundaries

Settings parity requires the exposed configuration sections **plus** host profiles and macro authoring, not merely 57 assignment choices. Safe offline work includes portable profile/macro JSON authoring and complete editors. The power units above describe the OEM UI's actual formatting; translating them into physical timer duration additionally requires firmware timer-frequency evidence. Device writes require exact C9 layouts, fresh target/profile association, preservation of unrelated bytes, and readback verification where the protocol permits it. The user's existing restriction remains: ordinary keyboard `0` must work before a real macro upload. No firmware/loader transition is authorized without independently verified recovery.

## Integration update

The new Mouse tab now includes the settings UI, explicit Apply, native-range
controls, active/count profile controls, portable named profiles with an optional
full button table, and local macro authoring/recording. Narrow changes preserve
unknown profile bytes. Reserved DPI7/8 and colour metadata are additional explicit
controls; they are not a claim of visible RGB hardware on this mouse.

A complete wired read of GET0A120/0452/069/078/0859/0C8 passed on the actual mouse.
Standby was changed10→11 and restored11→10. After the successful restore, the whole
logical117 body, full button table and raw profile record matched the initial
backup; debounce stayed8ms. One intermediate readback returned an exact previous
key body under a new all-data header; strict decoding stopped that transaction.
See READ-HANDSHAKE.md. Other setting types and profile switching still require
individual hardware validation. Receiver long reads are not qualified.

Remaining differences: local macro upload is disabled pending ordinary-key output
validation; the UI is Russian; OEM binary library import and factory reset are
not implemented. No verified factory snapshot is available and no reset request
is sent. The independent JSON formats are documented in the README/MACROS.md.
