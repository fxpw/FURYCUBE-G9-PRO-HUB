# G9 PRO HUB frontend

Vue 3 + TypeScript desktop interface for the native Windows HUB. The production app runs inside WebView2 at the host's local `https://g9.local` origin; it does not need a development server, CDN, or internet access.

```powershell
npm ci
npm run build
```

The build writes `dist/`. The native host packages this directory and provides device operations. Opening the UI in an ordinary browser shows an explicitly labelled offline editor; it never reports a connected device or sends HID commands.

For frontend development:

```powershell
npm run dev
npm run typecheck
npm run test
npm run format
```

The Playwright tests use installed Microsoft Edge and a synthetic WebView bridge. They do not connect to the mouse, registry, updater, or running desktop HUB. The test development server is bound to loopback and stopped after the suite. Screenshots and traces are ignored under `test-results/`.

TypeScript is pinned to 5.9.3 because the installed `vue-tsc` release relies on the compiler's `lib/tsc` entry point, which TypeScript 7 no longer exports. Runtime and build dependencies are pinned in `package-lock.json`.

## Source layout

- `src/App.vue`: desktop shell, navigation, connection status.
- `src/components/AssignmentsView.vue`: all actions supplied by the native catalog, modifiers, keyboard usage, existing hardware macro slots.
- `src/components/DeviceSettingsView.vue`: native metadata driven fields, independent drafts, explicit preset button-map restore.
- `src/components/ScreenEditor.vue`: native PNG preview, draggable/resizable layers, image conversion, widgets and live control. Browser text preview is illustrative and has no sensor data.
- `src/components/MacroEditor.vue`: portable local macro editing; hardware upload is deferred.
- `src/components/ProfilesView.vue`: local settings presets and JSON import/export.
- `src/components/PreferencesView.vue`: native startup preferences and diagnostic log.
- `src/styles.css`: theme, layout, responsive rules and control styles; no remote assets.
- `src/types.ts`, `src/bridge.ts`, `src/store.ts`: native DTO contract, correlated requests and application state.

## Native bridge

Requests use `window.chrome.webview.postMessage({ id, method, payload })`, with a unique string `id` and a 45 second timeout. Native responses are `{ id, ok, data, error }`; pushed state uses `{ event: "state", data: AppState }`. Unknown IDs are ignored. The native host validates origin, method names and payloads before performing an operation.

Screen DTOs retain the actual `ScreenProject` PascalCase field names and numeric enums. Widget kinds are the order declared in `G9Screen.cs` (0–9); composition is Image=0, Widgets=1, ImageAndWidgets=2; image resize is Fit=0, Crop=1, Stretch=2. The preview field `screen.pngBase64` accepts the native complete PNG data URL. Preview updates are debounced, serialized and flushed before upload or native file dialogs.

Macro documents use the existing portable lowercamel JSON schema (`name`, `repeatMode`, `repeatCount`, `steps`); they are never confused with on-device slots. Settings and button mappings use native lowercamel readback DTOs. A preset's `profileDraft.values` stays separate from actual device settings, so changes remain reviewable until Apply.

UI upload availability follows the qualified transport: OL4 requires USB, OL5 permits USB or 2.4 GHz. Native code independently enforces the same boundary. This frontend does not flash firmware, promise image persistence, or imply recovery guarantees.

## Interface languages

Russian is the default. Choose English or Russian in Settings or at the bottom of the sidebar. The choice persists in local storage (`g9.hub.locale`) and applies immediately without recreating editor components or discarding drafts. User profile names, macro names, image paths, artwork text and the screen project's culture remain unchanged.

`src/i18n.ts` contains the typed locale state, English UI dictionary and native-catalog adapters. Action labels use stable action IDs; settings labels and unsupported-feature explanations use field IDs. Every native request includes `payload.locale`; a language change also requests `app.state`, allowing the host to update its tray and file-dialog captions without accessing the device.

Technical device messages, Windows errors and raw diagnostic logs retain their original language. Their surrounding interface is localized, and the log section explains this boundary. Tests cover all six English views, all native action IDs, metadata options, return to Russian, persisted language choice, unchanged user content and unsaved editor drafts.
