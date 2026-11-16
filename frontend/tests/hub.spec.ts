import { test, expect, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import type { AppState } from "../src/types";

function fixture(): AppState {
  return {
    app: { autostart: false, resumeOled: false, busy: false },
    device: {
      connected: true,
      connection: "usb",
      name: "G9 PRO",
      firmware: "1.08 / OL5",
      profile: 1,
      settingsAvailable: true,
      lastReadAt: "2026-11-16T00:00:00Z",
    },
    mapping: {
      profile: 1,
      buttons: Array.from({ length: 6 }, (_, i) => ({
        index: i,
        name: `Кнопка ${i + 1}`,
        physicalLabel: [
          "Левая кнопка",
          "Правая кнопка",
          "Колёсико",
          "Вперёд",
          "Назад",
          "Кнопка-экран",
        ][i],
        visible: true,
        function: 2,
        param1: 0,
        param2: 0,
        bindingId: "left",
      })),
    },
    bindingActions: [
      {
        id: "left",
        name: "Левый клик",
        category: "Мышь",
        function: 2,
        defaultParam1: 0,
        defaultParam2: 0,
        parameterKind: "None",
      },
      {
        id: "keyboard",
        name: "Одиночная клавиша или сочетание",
        category: "Клавиатура",
        function: 17,
        defaultParam1: 0,
        defaultParam2: 39,
        parameterKind: "Keyboard",
      },
      {
        id: "macro",
        name: "Макрос из памяти мыши",
        category: "Макрос",
        function: 18,
        defaultParam1: 0,
        defaultParam2: 13,
        parameterKind: "Macro",
      },
    ],
    keyboardKeys: [
      { name: "0", usage: 39 },
      { name: "A", usage: 4 },
    ],
    settings: {
      profile: 1,
      readAtUtc: "2026-11-16T00:00:00Z",
      fields: [
        {
          id: "dpi.value1",
          name: "DPI: уровень 1",
          type: "dpi",
          value: "1600",
          unit: "DPI",
          min: 50,
          max: 22000,
          step: 50,
          options: [],
          canWrite: true,
        },
        {
          id: "dpi.enabledMask",
          name: "Уровни DPI",
          type: "integer",
          value: "63",
          unit: "",
          min: 1,
          max: 255,
          step: 1,
          options: [],
          canWrite: true,
        },
        {
          id: "sensor.angleSnap",
          name: "Коррекция углов",
          type: "choice",
          value: "0",
          unit: "",
          min: 0,
          max: 1,
          step: 1,
          options: [
            { value: "0", name: "Выкл." },
            { value: "1", name: "Вкл." },
          ],
          canWrite: true,
        },
        {
          id: "sensor.lod",
          name: "Высота отрыва (LOD)",
          type: "unsupported",
          value: null,
          unit: "",
          min: null,
          max: null,
          step: null,
          options: [],
          canWrite: false,
          unsupportedReason: "Настройка PAW3311 не подтверждена",
        },
      ],
    },
    screen: {
      project: {
        Version: 1,
        Width: 120,
        Height: 28,
        CultureName: "ru-RU",
        ImageFile: null,
        ImageX: 0,
        ImageY: 0,
        ImageWidth: 120,
        ImageHeight: 28,
        ImageOptions: {
          Resize: 0,
          Threshold: 128,
          Dither: false,
          Invert: false,
          BackgroundLit: false,
        },
        Composition: 1,
        Widgets: [
          {
            Id: "clock",
            Kind: 1,
            X: 2,
            Y: 3,
            Width: 55,
            Height: 12,
            Text: "",
            Format: "HH:mm",
            FontSize: 9,
            Bold: false,
            Lit: true,
            ClearBackground: true,
            ShowLabel: true,
          },
        ],
      },
      pngBase64: "",
      status: "Офлайн-тест: устройство не используется",
      live: false,
      widgetNames: [],
    },
    macros: [
      {
        name: "Клавиша 0",
        repeatMode: "Counted",
        repeatCount: 1,
        steps: [
          { kind: "KeyDown", virtualKey: 48 },
          { kind: "Delay", milliseconds: 50 },
          { kind: "KeyUp", virtualKey: 48 },
        ],
      },
    ],
    profiles: [{ id: "work", name: "Работа" }],
    logs: ["Synthetic bridge fixture; no device operations"],
  };
}

async function desktop(page: Page) {
  await page.addInitScript((initial: AppState) => {
    const native = {
      state: initial,
      requests: [] as { id: string; method: string; payload: any }[],
      listeners: [] as ((event: { data: any }) => void)[],
    };
    (window as any).__native = native;
    const send = (message: unknown) =>
      native.listeners.forEach((listener) => listener({ data: message }));
    (window as any).__pushState = () =>
      send({ event: "state", data: JSON.parse(JSON.stringify(native.state)) });
    const webview = {
      addEventListener(
        _type: string,
        listener: (event: { data: any }) => void,
      ) {
        native.listeners.push(listener);
      },
      postMessage(request: { id: string; method: string; payload: any }) {
        native.requests.push(request);
        let result: unknown = native.state;
        if (request.method === "screen.update") {
          native.state.screen.project = request.payload.project;
          result = native.state.screen;
        }
        if (request.method === "screen.live")
          native.state.screen.live = request.payload.enabled;
        if (request.method === "device.settings") {
          for (const change of request.payload.changes) {
            const field = native.state.settings!.fields.find(
              (item) => item.id === change.id,
            );
            if (field) field.value = change.value;
          }
          native.state.profileDraft = null;
          native.state.settings!.readAtUtc = new Date().toISOString();
        }
        if (request.method === "profiles.open")
          native.state.profileDraft = {
            schemaVersion: 1,
            name: "Работа",
            values: { "dpi.value1": "3200" },
            keymapHex: "test-fixture-never-sent-to-hardware",
          };
        if (request.method === "app.preferences")
          Object.assign(native.state.app, request.payload);
        queueMicrotask(() =>
          send({
            id: request.id,
            ok: true,
            data: JSON.parse(JSON.stringify(result)),
          }),
        );
      },
    };
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview },
    });
  }, fixture());
  await page.goto("/");
  await expect(page.locator(".device-status")).toContainText("USB");
}
async function requests(page: Page, method: string) {
  return page.evaluate(
    (m) => (window as any).__native.requests.filter((r: any) => r.method === m),
    method,
  );
}

test("browser preview never claims a connected mouse; all sections render", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto("/");
  await expect(
    page.getByText("Предпросмотр интерфейса в браузере"),
  ).toBeVisible();
  await expect(page.locator(".device-status")).toContainText("Не подключена");
  await page.getByRole("button", { name: "Экран OLED", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "На OLED", exact: true }),
  ).toBeDisabled();
  await expect(page.locator(".oled-stage canvas")).toBeVisible();
  await page.locator(".add-layer select").selectOption("0");
  await page.getByRole("button", { name: "Виджет", exact: true }).click();
  await expect(page.locator(".layer-list .layer-row")).toHaveCount(2);
  await page.screenshot({
    path: "test-results/browser-oled.png",
    fullPage: true,
  });
  for (const name of [
    "DPI и сенсор",
    "Макросы",
    "Профили",
    "Настройки",
    "Назначения",
  ]) {
    await page.getByRole("button", { name, exact: true }).click();
    await expect(page.locator("h1")).toHaveText(name);
  }
  expect(errors).toEqual([]);
});

test("keyboard binding sends exact native action and modifier mask", async ({
  page,
}) => {
  await desktop(page);
  await page.getByRole("button", { name: /Кнопка-экран/ }).click();
  await page.locator(".binding-editor select").first().selectOption("keyboard");
  await page.getByLabel("Левый Ctrl", { exact: true }).check();
  await page.getByRole("button", { name: "Применить", exact: true }).click();
  const sent = await requests(page, "device.assign");
  expect(sent).toHaveLength(1);
  expect(sent[0].payload).toEqual({
    profile: 1,
    index: 5,
    actionId: "keyboard",
    param1: 1,
    param2: 39,
    locale: "ru",
  });
  expect(typeof sent[0].id).toBe("string");
});

test("identical native pushes preserve pending setting edits; apply uses readback profile", async ({
  page,
}) => {
  await desktop(page);
  await page.getByRole("button", { name: "DPI и сенсор", exact: true }).click();
  const input = page.locator(
    'input[type="number"][aria-label="DPI: уровень 1"]',
  );
  await input.fill("3200");
  await input.blur();
  await page.evaluate(() => (window as any).__pushState());
  await expect(input).toHaveValue("3200");
  await expect(
    page.getByRole("button", { name: "Применить 1", exact: true }),
  ).toBeEnabled();
  await page.getByRole("button", { name: "Применить 1", exact: true }).click();
  const sent = await requests(page, "device.settings");
  expect(sent[0].payload).toEqual({
    profile: 1,
    changes: [{ id: "dpi.value1", value: "3200" }],
    includeButtons: false,
    locale: "ru",
  });
});

test("preset values are staged against real baseline; keymap restore requires explicit checkbox", async ({
  page,
}) => {
  await desktop(page);
  await page.getByRole("button", { name: "Профили", exact: true }).click();
  await page.getByRole("button", { name: "Открыть", exact: true }).click();
  await expect(page.locator("h1")).toHaveText("DPI и сенсор");
  await expect(
    page.locator('input[type="number"][aria-label="DPI: уровень 1"]'),
  ).toHaveValue("3200");
  expect(
    await page.evaluate(
      () => (window as any).__native.state.settings.fields[0].value,
    ),
  ).toBe("1600");
  await expect(
    page.getByLabel("Также применить назначения кнопок из профиля"),
  ).not.toBeChecked();
  await page.getByLabel("Также применить назначения кнопок из профиля").check();
  await page.getByRole("button", { name: "Применить 1", exact: true }).click();
  expect(
    (await requests(page, "device.settings"))[0].payload.includeButtons,
  ).toBe(true);
});

test("OLED debounces native previews and flushes latest Pascal DTO before upload", async ({
  page,
}) => {
  await desktop(page);
  await page.getByRole("button", { name: "Экран OLED", exact: true }).click();
  const x = page.locator(
    'input[type="number"][aria-label="По горизонтали · X"]',
  );
  await x.fill("18");
  await x.blur();
  const width = page.locator('input[type="number"][aria-label="Ширина"]');
  await width.fill("84");
  await width.blur();
  await page.getByRole("button", { name: "На OLED", exact: true }).click();
  const sent = await page.evaluate(() => (window as any).__native.requests);
  const update = sent.filter((r: any) => r.method === "screen.update").at(-1);
  expect(update.payload.project.Widgets[0].X).toBe(18);
  expect(update.payload.project.Widgets[0].Width).toBe(84);
  expect(typeof update.payload.project.Widgets[0].Kind).toBe("number");
  expect(sent.at(-1).method).toBe("screen.upload");
});

test("OL4 radio permits editor but blocks unqualified OLED upload", async ({
  page,
}) => {
  await desktop(page);
  await page.evaluate(() => {
    const n = (window as any).__native;
    n.state.device.connection = "radio";
    n.state.device.firmware = "1.08 / OL4";
    (window as any).__pushState();
  });
  await page.getByRole("button", { name: "Экран OLED", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "На OLED", exact: true }),
  ).toBeDisabled();
  await expect(page.getByText(/Загрузка OLED требует прошивки/)).toBeVisible();
  expect(await requests(page, "screen.upload")).toHaveLength(0);
});

test("local macro save uses portable lowercamel schema and leaves upload unavailable", async ({
  page,
}) => {
  await desktop(page);
  await page.getByRole("button", { name: "Макросы", exact: true }).click();
  await page.getByRole("button", { name: /Клавиша 0/ }).click();
  await page.getByLabel("Название", { exact: true }).fill("Новая клавиша");
  await page.getByRole("button", { name: "Сохранить", exact: true }).click();
  const sent = await requests(page, "macros.save");
  expect(sent[0].payload.originalName).toBe("Клавиша 0");
  expect(sent[0].payload.document.name).toBe("Новая клавиша");
  expect(sent[0].payload.document.steps[1]).toEqual({
    kind: "Delay",
    milliseconds: 50,
  });
  await expect(
    page.getByText(/Загрузка новых макросов в память мыши пока не подключена/),
  ).toBeVisible();
});

test("native preference state drives switch after successful reply", async ({
  page,
}) => {
  await desktop(page);
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  const autostart = page.getByLabel("Запускать HUB вместе с Windows", {
    exact: false,
  });
  await autostart.check();
  expect((await requests(page, "app.preferences"))[0].payload).toEqual({
    autostart: true,
    locale: "ru",
  });
  await expect(autostart).toBeChecked();
});

test("OLED pointer drag and corner resize update independent layer geometry", async ({
  page,
}) => {
  await desktop(page);
  await page.getByRole("button", { name: "Экран OLED", exact: true }).click();
  const stage = await page.locator(".oled-stage").boundingBox();
  const layer = await page.locator(".layer-rect.selected").boundingBox();
  expect(stage).not.toBeNull();
  expect(layer).not.toBeNull();
  await page.mouse.move(layer!.x + 20, layer!.y + 15);
  await page.mouse.down();
  await page.mouse.move(layer!.x + 20 + stage!.width / 12, layer!.y + 15, {
    steps: 4,
  });
  await page.mouse.up();
  await expect(
    page.locator('input[type="number"][aria-label="По горизонтали · X"]'),
  ).toHaveValue("12");
  const handle = await page
    .locator(".layer-rect.selected .resize-handle")
    .boundingBox();
  await page.mouse.move(
    handle!.x + handle!.width / 2,
    handle!.y + handle!.height / 2,
  );
  await page.mouse.down();
  await page.mouse.move(
    handle!.x + handle!.width / 2 + stage!.width / 24,
    handle!.y + handle!.height / 2 + stage!.height / 14,
    { steps: 3 },
  );
  await page.mouse.up();
  await expect(
    page.locator('input[type="number"][aria-label="Ширина"]'),
  ).toHaveValue("60");
  await expect(
    page.locator('input[type="number"][aria-label="Высота"]'),
  ).toHaveValue("14");
  await page.getByRole("button", { name: "На OLED", exact: true }).click();
  const sent = await requests(page, "screen.update");
  expect(sent.at(-1).payload.project.Widgets[0]).toMatchObject({
    X: 12,
    Y: 3,
    Width: 60,
    Height: 14,
  });
});

test("native error keeps preference unchanged and shows the actual error", async ({
  page,
}) => {
  await desktop(page);
  await page.evaluate(() => {
    const native = (window as any).__native;
    const transport = (window as any).chrome.webview;
    const original = transport.postMessage.bind(transport);
    transport.postMessage = (request: any) => {
      if (request.method !== "app.preferences") return original(request);
      queueMicrotask(() =>
        native.listeners.forEach((listener: any) =>
          listener({
            data: {
              id: request.id,
              ok: false,
              error: "Тест: автозапуск не изменён",
            },
          }),
        ),
      );
    };
  });
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  const input = page.getByLabel("Запускать HUB вместе с Windows", {
    exact: false,
  });
  await input.click();
  await expect(page.getByRole("status")).toContainText(
    "Тест: автозапуск не изменён",
  );
  await expect(input).not.toBeChecked();
});

test("English covers all six browser views, persists reload and switches back to Russian", async ({
  page,
}) => {
  await page.goto("/");
  await page.locator(".language-control select").selectOption("en");
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await expect(page.locator("h1")).toHaveText("Button assignments");
  for (const name of [
    "Button assignments",
    "DPI & sensor",
    "OLED display",
    "Macros",
    "Profiles",
    "Settings",
  ]) {
    await page.getByRole("button", { name, exact: true }).click();
    await expect(page.locator("h1")).toHaveText(name);
    const text = (await page.locator("body").innerText()).replaceAll(
      "Русский",
      "",
    );
    expect(
      text.match(/[А-Яа-яЁё]+/g),
      `Untranslated text in ${name}: ${text}`,
    ).toBeNull();
  }
  await expect(page.locator(".language-preference select")).toHaveValue("en");
  await page.reload();
  await expect(page.locator("h1")).toHaveText("Button assignments");
  await expect(page.locator(".language-control select")).toHaveValue("en");
  await page.getByRole("button", { name: "OLED display", exact: true }).click();
  await page.screenshot({
    path: "test-results/english-oled.png",
    fullPage: true,
  });
  await page.locator(".language-control select").selectOption("ru");
  await expect(page.locator("html")).toHaveAttribute("lang", "ru");
  await expect(page.locator("h1")).toHaveText("Экран OLED");
});

test("English translates native action catalog and setting metadata without renaming user content", async ({
  page,
}) => {
  await desktop(page);
  const bindings = readFileSync(
    new URL("../../src/G9Bindings.cs", import.meta.url),
    "utf8",
  );
  const catalog = [
    ...bindings.matchAll(/Action\("([^"]+)",\s*"([^"]+)",\s*"([^"]+)"/g),
  ].map((match) => ({
    id: match[1],
    name: match[2],
    category: match[3],
    function: 2,
    defaultParam1: 0,
    defaultParam2: 0,
    parameterKind: "None",
  }));
  await page.evaluate((items) => {
    (window as any).__native.state.bindingActions = items;
    (window as any).__native.state.settings.fields.push({
      id: "active-profile",
      name: "Активный профиль",
      type: "choice",
      value: "1",
      unit: "",
      min: 1,
      max: 5,
      step: 1,
      canWrite: true,
      options: Array.from({ length: 5 }, (_, i) => ({
        value: String(i + 1),
        name: `Профиль ${i + 1}`,
      })),
    });
    (window as any).__pushState();
  }, catalog);
  await page.locator(".language-control select").selectOption("en");
  await expect(page.locator(".button-list")).toContainText("Left button");
  const options = await page
    .locator(".binding-editor select")
    .first()
    .textContent();
  expect(options).not.toMatch(/[А-Яа-яЁё]/);
  expect(options).toContain("Single key or shortcut");
  expect(
    await page.locator(".binding-editor optgroup").allTextContents(),
  ).toHaveLength(new Set(catalog.map((item) => item.category)).size);
  await page.getByRole("button", { name: "DPI & sensor", exact: true }).click();
  await expect(
    page.locator('input[type="number"][aria-label="DPI: stage 1"]'),
  ).toHaveValue("1600");
  await page
    .getByRole("tab", { name: "Sensor & response", exact: true })
    .click();
  await expect(page.getByText("Angle snapping", { exact: true })).toBeVisible();
  await expect(
    page.getByText("Lift-off distance (LOD)", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Hidden in the stock G9/C9 UI; the exact PAW3311 setting has not been verified.",
    ),
  ).toBeVisible();
  await page.getByRole("tab", { name: "Profile & more", exact: true }).click();
  const profileOptions = page.locator(".settings-panel select");
  await expect(profileOptions).toContainText("Profile 1");
  await expect(profileOptions).toContainText("Profile 5");
  expect(await profileOptions.textContent()).not.toMatch(/[А-Яа-яЁё]/);
  await page.getByRole("button", { name: "Macros", exact: true }).click();
  await expect(page.getByRole("button", { name: /Клавиша 0/ })).toBeVisible();
  await page.getByRole("button", { name: /Клавиша 0/ }).click();
  await expect(page.getByLabel("Name", { exact: true })).toHaveValue(
    "Клавиша 0",
  );
  await page.getByRole("button", { name: "Profiles", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Работа", exact: true }),
  ).toBeVisible();
  const localeRequests = await requests(page, "app.state");
  expect(
    localeRequests.some((request: any) => request.payload.locale === "en"),
  ).toBe(true);
});

test("language changes retain unsaved settings and screen geometry/text drafts", async ({
  page,
}) => {
  await desktop(page);
  await page.getByRole("button", { name: "DPI и сенсор", exact: true }).click();
  const dpi = page.locator('input[type="number"][aria-label="DPI: уровень 1"]');
  await dpi.fill("3200");
  await dpi.blur();
  await page.getByRole("button", { name: "Экран OLED", exact: true }).click();
  const x = page.locator(
    'input[type="number"][aria-label="По горизонтали · X"]',
  );
  await x.fill("18");
  await x.blur();
  await page.locator(".add-layer select").selectOption("0");
  await page.getByRole("button", { name: "Виджет", exact: true }).click();
  await page.locator(".layer-properties textarea").fill("Мой текст");
  await page.locator(".language-control select").selectOption("en");
  await expect(page.locator("h1")).toHaveText("OLED display");
  await expect(page.locator(".layer-properties textarea")).toHaveValue(
    "Мой текст",
  );
  await page.getByRole("button", { name: "DPI & sensor", exact: true }).click();
  await expect(
    page.locator('input[type="number"][aria-label="DPI: stage 1"]'),
  ).toHaveValue("3200");
  await page.locator(".language-control select").selectOption("ru");
  await expect(
    page.locator('input[type="number"][aria-label="DPI: уровень 1"]'),
  ).toHaveValue("3200");
  await page.getByRole("button", { name: "Экран OLED", exact: true }).click();
  await expect(page.locator(".layer-properties textarea")).toHaveValue(
    "Мой текст",
  );
  await page.getByRole("button", { name: /Часы/ }).click();
  await expect(
    page.locator('input[type="number"][aria-label="По горизонтали · X"]'),
  ).toHaveValue("18");
  expect(await requests(page, "device.settings")).toHaveLength(0);
  expect(await requests(page, "screen.upload")).toHaveLength(0);
  expect(await requests(page, "app.preferences")).toHaveLength(0);
});
