import { computed, ref, watch } from "vue";
import type { BindingAction, ButtonMapping, SettingField } from "./types";

export type Locale = "ru" | "en";
const storageKey = "g9.hub.locale";
function initialLocale(): Locale {
  try {
    return localStorage.getItem(storageKey) === "en" ? "en" : "ru";
  } catch {
    return "ru";
  }
}
export const locale = ref<Locale>(initialLocale());
export const localeTag = computed(() =>
  locale.value === "en" ? "en-GB" : "ru-RU",
);
watch(
  locale,
  (value) => {
    document.documentElement.lang = value;
    try {
      localStorage.setItem(storageKey, value);
    } catch {
      /* The choice still applies for this session. */
    }
    window.dispatchEvent(new Event("g9:locale"));
  },
  { immediate: true },
);

// Russian source keys keep existing labels easy to locate. Only explicit UI
// messages pass through t(); user names, artwork text and raw logs never do.
export const english: Record<string, string> = {
  "Язык интерфейса": "Interface language",
  "Выберите язык приложения. Названия профилей, макросов и текст на экране не меняются.":
    "Choose the application language. Profile names, macro names and screen text stay unchanged.",
  "Технические сообщения устройства и Windows показаны на языке источника.":
    "Technical device and Windows messages are shown in their original language.",
  "Системное сообщение": "System message",
  "+ Картинка": "+ Image",
  ". Редактор доступен.": ". The editor is available.",
  "120 × 28 пикселей": "120 × 28 pixels",
  "DPI и сенсор": "DPI & sensor",
  "USB · кабель": "USB · wired",
  "{0} изменений": "{0} changes",
  "{0} мс": "{0} ms",
  "· штатный для кнопки-экрана": "· default screen-button slot",
  "Активный уровень должен оставаться включённым.":
    "The active DPI stage must remain enabled.",
  Библиотека: "Library",
  "Браузер · редактор без устройства": "Browser · offline editor",
  "Ваш экран. Ваши виджеты.": "Your screen. Your widgets.",
  "Введите имя макроса": "Enter a macro name",
  "Введите название профиля": "Enter a profile name",
  Виджет: "Widget",
  "Включение / выключение": "Toggle on / off",
  "Включённые уровни DPI": "Enabled DPI stages",
  "Возобновлять виджеты, когда поддерживаемая мышь подключена.":
    "Resume widgets when a supported mouse is connected.",
  Вперёд: "Forward",
  "Вписать целиком": "Fit entire image",
  "Все действия": "All actions",
  "Всегда под рукой": "Always within reach",
  "Выберите действие": "Choose an action",
  "Выберите кнопку на схеме или в списке.":
    "Choose a button on the diagram or in the list.",
  "Выберите слой": "Select a layer",
  "Выберите слой в списке или добавьте новый.":
    "Select a layer from the list or add a new one.",
  "Выбрать кнопку {0}": "Select button {0}",
  "Выбрать нажатием": "Press a key to choose",
  Высота: "Height",
  Выше: "Move up",
  "Границы слоёв": "Layer outlines",
  "Дата · 16.11": "Date · 16.11",
  "Дата и время": "Date and time",
  Действие: "Action",
  "Демонстрационный макет без системных данных":
    "Demo layout without system readings",
  "Демонстрация · значения датчиков не подключены":
    "Demo · sensor readings unavailable",
  "Дизеринг · передать полутона точками":
    "Dithering · represent shades with dots",
  "Длительность паузы, мс": "Delay duration, ms",
  "Для клавиши нужны отдельные нажатие и отпускание. Между ними можно поставить паузу.":
    "A key needs separate press and release actions. You can add a delay between them.",
  "До 20 символов": "Up to 20 characters",
  Добавить: "Add",
  "Добавляемый виджет": "Widget to add",
  "Добавьте картинку или первый виджет.": "Add an image or your first widget.",
  "Добавьте первое действие": "Add your first action",
  "Дождитесь завершения текущей операции":
    "Wait for the current operation to finish",
  "Есть несохранённые изменения": "Unsaved changes",
  "Жирный шрифт": "Bold text",
  "Журнал пока пуст.": "The log is empty.",
  "Журнал работы": "Activity log",
  "Завершить HUB": "Quit HUB",
  "Загрузка CPU": "CPU usage",
  "Загрузка GPU": "GPU usage",
  "Загрузка RAM": "RAM usage",
  "Загрузка OLED сейчас доступна по USB с OL4 или OL5. Передача через приёмник временно отключена. Текущая:":
    "OLED upload is currently available over USB with OL4 or OL5. Receiver transfers are temporarily disabled. Current firmware:",
  "Заданное количество": "Repeat count",
  "Закрыть уведомление": "Dismiss notification",
  "Заменить изображение": "Replace image",
  "Запись в профиль": "Write to profile",
  "Заполнить с обрезкой": "Fill and crop",
  "Запуск и поведение HUB": "HUB startup and behaviour",
  "Запускать HUB вместе с Windows": "Start HUB with Windows",
  "Заряд батареи": "Battery level",
  "Здесь будут ваши профили": "Your profiles will appear here",
  "Значения из памяти мыши · профиль": "Values read from the mouse · profile",
  "Изменение активного профиля или количества профилей примените отдельно кнопкой рядом с полем.":
    "Apply the active profile or profile count separately using the button beside the field.",
  "Изменения показаны поверх текущих настроек. Нажмите «Применить», чтобы записать выбранные значения.":
    "Changes are staged against the current settings. Click Apply to write the selected values.",
  Изображение: "Image",
  "Изображение и виджеты": "Image and widgets",
  "Импорт JSON": "Import JSON",
  "Инвертировать цвета": "Invert colours",
  "КНОПКИ МЫШИ": "MOUSE BUTTONS",
  "Каждая кнопка — по вашему сценарию": "Make every button work your way",
  Клавиша: "Key",
  "Кнопка 6 — маленький экран на корпусе.":
    "Button 6 is the small screen on the mouse.",
  "Кнопка мыши": "Mouse button",
  "Код клавиши Windows (VK)": "Windows key code (VK)",
  Количество: "Count",
  Колёсико: "Wheel",
  Копировать: "Copy",
  Копия: "Duplicate",
  "ЛОКАЛЬНАЯ БИБЛИОТЕКА": "LOCAL LIBRARY",
  "ЛОКАЛЬНЫЙ ПРОЕКТ": "LOCAL PROJECT",
  Левая: "Left",
  "Левый Alt": "Left Alt",
  "Левый Ctrl": "Left Ctrl",
  "Левый Shift": "Left Shift",
  "Левый Win": "Left Win",
  "Макрос сохранён локально": "Macro saved locally",
  Макросы: "Macros",
  Масштабирование: "Scaling",
  Модификаторы: "Modifiers",
  "Мои профили": "My profiles",
  Мышь: "Mouse",
  "На OLED": "Upload to OLED",
  "Нажать клавишу": "Press a key",
  "Нажать кнопку мыши": "Press a mouse button",
  "Нажмите клавишу…": "Press a key…",
  Назад: "Back",
  Название: "Name",
  "Название профиля": "Profile name",
  "Назначает макрос, который уже находится в памяти. Локальный редактор последовательностей не загружает его в мышь.":
    "Assigns a macro already stored on the mouse. The local sequence editor does not upload it to the device.",
  "Назначение записано и проверено": "Assignment written and verified",
  Назначения: "Button assignments",
  "Найти мышь": "Find mouse",
  "Например, работа или игры": "For example, work or gaming",
  "Настольное приложение": "Desktop application",
  "Настройка мыши и редактор OLED. Аппаратные возможности зависят от установленной прошивки и способа подключения.":
    "Mouse settings and OLED editor. Hardware features depend on the installed firmware and connection.",
  "Настройка профиля применена": "Profile setting applied",
  Настройки: "Settings",
  "Настройки ещё не прочитаны": "Settings have not been read yet",
  "Настройки мыши": "Mouse settings",
  "Настройки прочитаны": "Settings read",
  "Настройки прочитаны после записи": "Settings verified after writing",
  "Нативная отрисовка": "Native rendering",
  "Не поддерживается": "Not supported",
  "Не подключена": "Not connected",
  "Неизвестное назначение": "Unknown assignment",
  "Несохранённые изменения будут потеряны. Продолжить?":
    "Unsaved changes will be lost. Continue?",
  Ниже: "Move down",
  "Новый макрос": "New macro",
  "Нужны данные устройства": "Device data required",
  Обновить: "Refresh",
  "Обновить соединение, прошивку, назначения и доступные настройки.":
    "Refresh the connection, firmware version, assignments and available settings.",
  "Обновляется…": "Updating…",
  "Обновлять в реальном времени": "Update live",
  "Ожидание устройства": "Waiting for device",
  "Окно закрыто": "Window closed",
  "Оставьте хотя бы один уровень DPI": "Keep at least one DPI stage enabled",
  Открыть: "Open",
  "Открыть проект": "Open project",
  "Отменить изменения": "Discard changes",
  "Отпустить клавишу": "Release a key",
  "Отпустить кнопку мыши": "Release a mouse button",
  "Отрисовка…": "Rendering…",
  "Очищать фон под текстом": "Clear background behind text",
  "ПОВЕДЕНИЕ ПРИЛОЖЕНИЯ": "APPLICATION BEHAVIOUR",
  "Параметр скоростного клика": "Rapid-click parameter",
  Пауза: "Delay",
  Переименовать: "Rename",
  "Перемещение мыши": "Move mouse",
  "Перетаскивайте слой мышью. Уголок меняет размер. Стрелки — сдвиг на 1 px, Shift + стрелки — на 5 px.":
    "Drag a layer to move it. Drag its corner to resize. Arrow keys move by 1 px; Shift + arrows move by 5 px.",
  Питание: "Power",
  "По вертикали · Y": "Vertical position · Y",
  "По горизонтали · X": "Horizontal position · X",
  Повтор: "Repeat",
  "Под разные задачи": "A profile for every task",
  "Подберите действие": "Choose an action",
  "Подключение мыши, системные показатели и запись на OLED доступны в настольном HUB. Здесь можно редактировать макет без отправки на устройство.":
    "Device connection, system readings and OLED upload are available in the desktop HUB. Here you can edit a layout without sending it to a device.",
  "Подключите G9 PRO по USB и нажмите «Найти мышь». Значения появятся после чтения устройства.":
    "Connect G9 PRO over USB and click Find mouse. Values appear after the device has been read.",
  "Подождите…": "Please wait…",
  "Поиск клавиши…": "Search keys…",
  "Пока кнопка удерживается": "While button is held",
  "Показать журнал": "Show log",
  "Показывать название показателя": "Show reading label",
  "По 2.4G доступны назначения, DPI, сенсор и частота опроса. Для таймеров питания и debounce подключите USB-кабель.":
    "Button assignments, DPI, sensor and polling rate are available over 2.4G. Connect USB for power timers and debounce.",
  "Положение изображения": "Image position",
  "Порог чёрного и белого": "Black and white threshold",
  "После обнаружения здесь появятся назначения, прочитанные из её памяти.":
    "Assignments read from the mouse will appear here after discovery.",
  "Последовательности действий": "Action sequences",
  Правая: "Right",
  "Правый Alt": "Right Alt",
  "Правый Ctrl": "Right Ctrl",
  "Правый Shift": "Right Shift",
  "Правый Win": "Right Win",
  Предпросмотр: "Preview",
  "Предпросмотр интерфейса в браузере": "Browser interface preview",
  "Предпросмотр появится после отрисовки":
    "The preview will appear after rendering",
  "Предпросмотр экрана, отрисованный приложением":
    "Screen preview rendered by the application",
  Приложение: "Application",
  "Приложение будет запускаться автоматически после входа.":
    "The application will start automatically when you sign in.",
  "Приложение не выполнило операцию":
    "The application could not complete the operation",
  "Приложение не ответило за 45 секунд. Проверьте соединение и журнал.":
    "The application did not respond within 45 seconds. Check the connection and log.",
  Применить: "Apply",
  "Применить отдельно": "Apply separately",
  Пробел: "Space",
  "Продолжать обновление OLED при запуске": "Resume OLED updates at startup",
  Прокрутка: "Scroll",
  "Прокрутка (120 = одно деление)": "Scroll (120 = one notch)",
  Профили: "Profiles",
  Профиль: "Profile",
  "Профиль и прочее": "Profile & more",
  "Профиль открыт как черновик": "Profile opened as a draft",
  "Профиль сохранён": "Profile saved",
  "Прочитайте мышь по USB и сохраните первый набор настроек.":
    "Read the mouse over USB and save your first set of settings.",
  Прочитано: "Read",
  "Прочитано:": "Last read:",
  "Прочитать мышь заново": "Read mouse again",
  "Прочитать устройство": "Read device",
  РЕДАКТИРОВАНИЕ: "EDIT ASSIGNMENT",
  "Рабочее пространство": "Workspace",
  "Радио · 2.4 ГГц": "Radio · 2.4 GHz",
  "Разделы HUB": "HUB sections",
  "Размер шрифта": "Font size",
  Растянуть: "Stretch",
  "Редактор сохраняет последовательности на компьютере. Загрузка новых макросов в память мыши пока не подключена. В разделе «Назначения» можно выбрать уже записанный аппаратный слот.":
    "The editor saves sequences on this computer. Uploading new macros to the mouse is not available yet. In Button assignments you can select an existing hardware slot.",
  "С секундами · 23:45:06": "With seconds · 23:45:06",
  "СВОЙСТВА СЛОЯ": "LAYER PROPERTIES",
  Свернуть: "Collapse",
  "Светлый текст": "Light text",
  "Светлый фон прозрачных областей": "Light background for transparent areas",
  "Сенсор и отклик": "Sensor & response",
  Слои: "Layers",
  Слот: "Slot",
  "Сначала подключите мышь": "Connect your mouse first",
  "Содержимое экрана": "Screen content",
  "Создайте первый макрос или импортируйте файл проекта.":
    "Create your first macro or import a project file.",
  "Создать макрос": "Create macro",
  "Сообщения приложения и устройства для диагностики.":
    "Application and device messages for diagnostics.",
  Сохранить: "Save",
  "Сохранить профиль": "Save profile",
  "Сохранить текущие настройки": "Save current settings",
  "Сохраняйте прочитанные настройки мыши и переносите профили через JSON. Открытый профиль сначала становится черновиком для проверки.":
    "Save settings read from your mouse and transfer profiles as JSON. An opened profile becomes a draft for review first.",
  "Сохранённые настройки для разных задач":
    "Saved settings for different tasks",
  "Схема кнопок G9 PRO": "G9 PRO button diagram",
  "Также применить назначения кнопок из профиля":
    "Also apply button assignments from this profile",
  Текст: "Text",
  "Температура CPU": "CPU temperature",
  "Температура GPU": "GPU temperature",
  "Тип виджета": "Widget type",
  "Тип нового действия": "New action type",
  "Только виджеты": "Widgets only",
  "Только изображение": "Image only",
  "Только модификаторы": "Modifiers only",
  Удалить: "Delete",
  "Удалить выбранный слой": "Delete selected layer",
  "Удалить действие {0}": "Delete action {0}",
  "Удалить локальный макрос «{0}»?": "Delete local macro “{0}”?",
  "Удалить макрос": "Delete macro",
  "Удалить слой": "Delete layer",
  "Удалить сохранённый профиль «{0}»? Настройки мыши сохранятся.":
    "Delete saved profile “{0}”? The mouse settings will be kept.",
  "Уже записанный слот в мыши": "Existing slot stored on the mouse",
  Уровень: "Stage",
  "Уровень DPI": "DPI stage",
  "Уровень DPI при удержании": "DPI stage while held",
  "Уровни DPI": "DPI stages",
  "Устройство требует внимания": "Device needs attention",
  "Формат часов": "Clock format",
  "ХОЛСТ OLED": "OLED CANVAS",
  Часы: "Clock",
  "Часы и минуты · 23:45": "Hours and minutes · 23:45",
  "Черновик:": "Draft:",
  "Чувствительность, отклик и питание": "Sensitivity, response and power",
  "Шаг 50 до 10 000 DPI, затем 100 DPI.":
    "50 DPI steps up to 10,000; 100 DPI steps above.",
  "Шаг DPI: 50 до 10 000, затем 100": "DPI steps: 50 up to 10,000, then 100",
  Ширина: "Width",
  "Экран OLED": "OLED display",
  Экспорт: "Export",
  "Экспорт PNG": "Export PNG",
  "Эта операция доступна в настольном HUB. В браузере открыт только редактор.":
    "This operation is available in the desktop HUB. The browser provides the editor only.",
  действий: "actions",
  "действий ·": "actions ·",
  "мс пауз": "ms of delays",
  "не определена": "unknown",
  "↑ Выше": "↑ Move up",
  "↓ Ниже": "↓ Move down",
  "Выкл.": "Off",
  "Вкл.": "On",
  Гц: "Hz",
  мс: "ms",
  с: "s",
  мин: "min",
  "Левая кнопка": "Left button",
  "Правая кнопка": "Right button",
  "Нажатие колеса": "Wheel click",
  "Кнопка экрана OLED": "OLED screen button",
  "Кнопка-экран": "Screen button",
  "Скрытый слот DPI": "Hidden DPI slot",
  "Скрытый слот режима": "Hidden mode slot",
  "Боковая «Назад»": "Side button · Back",
  "Боковая «Вперёд»": "Side button · Forward",
  "Колесо вниз": "Wheel down",
  "Колесо вверх": "Wheel up",
  "Резервный слот {0}": "Reserved slot {0}",
  "Контекстное меню": "Context menu",
  Клавиатура: "Keyboard",
  Макрос: "Macro",
  Мультимедиа: "Media",
  Браузер: "Browser",
  Приложения: "Applications",
  Сочетания: "Shortcuts",
  Дополнительно: "Advanced",
  "Исторический исходящий пакет; текущее состояние мыши не подтверждено.":
    "Historical outgoing packet; the current mouse state has not been confirmed.",
  "Нет достоверного исходного значения.":
    "No reliable original value is available.",
  "Исходное значение вне подтверждённого формата штатного G9; оно сохранено без подмены.":
    "The original value is outside the verified stock G9 format; it has been preserved unchanged.",
};

export function t(
  key: string | null | undefined,
  ...values: (string | number)[]
): string {
  const normalized = (key || "").replace(/\s+/g, " ").trim();
  const message =
    locale.value === "en" ? english[normalized] || normalized : normalized;
  return message.replace(/\{(\d+)\}/g, (_, index: string) =>
    String(values[Number(index)] ?? `{${index}}`),
  );
}

const actionEnglish: Record<string, string> = {
  left: "Left click",
  right: "Right click",
  middle: "Middle click",
  forward: "Forward",
  back: "Back",
  "double-click": "Double left click",
  fire: "Rapid fire",
  "scroll-up": "Scroll up",
  "scroll-down": "Scroll down",
  "tilt-left": "Scroll left",
  "tilt-right": "Scroll right",
  off: "Disable button",
  "dpi-cycle": "Cycle DPI",
  "dpi-up": "DPI +",
  "dpi-down": "DPI −",
  sniper: "Sniper button",
  keyboard: "Single key or shortcut",
  macro: "Macro stored on the mouse",
  "media-player": "Open media player",
  "previous-track": "Previous track",
  "next-track": "Next track",
  "play-pause": "Play / pause",
  "media-stop": "Stop playback",
  mute: "Mute",
  "volume-up": "Volume +",
  "volume-down": "Volume −",
  "browser-home": "Browser home page",
  "browser-favorites": "Browser favourites",
  "browser-favorites-consumer": "Favourites (media command)",
  "browser-forward": "Browser forward",
  "browser-back": "Browser back",
  "browser-stop": "Stop loading page",
  "browser-refresh": "Refresh page",
  "browser-search": "Browser search",
  email: "Email",
  calculator: "Calculator",
  "my-computer": "This PC",
  cut: "Cut",
  copy: "Copy",
  paste: "Paste",
  open: "Open",
  save: "Save",
  find: "Find",
  redo: "Redo",
  "select-all": "Select all",
  print: "Print",
  "close-window": "Close window",
  "switch-windows": "Switch window",
  desktop: "Show desktop",
  run: "Run…",
  lock: "Lock computer",
  screenshot: "Screenshot",
  "led-cycle": "Cycle lighting",
  "profile-cycle": "Cycle profile",
  "profile-up": "Next profile",
  "profile-down": "Previous profile",
  "mode-key": "Switch mode",
};
export function actionName(action: BindingAction): string {
  return locale.value === "en"
    ? actionEnglish[action.id] || t(action.name)
    : action.name;
}
export function buttonName(button: ButtonMapping): string {
  const label = button.physicalLabel || button.name;
  if (locale.value === "ru") return label;
  const parts = /^(\d+\.\s*)(.+)$/.exec(label);
  const name = parts ? parts[2] : label;
  const reserved = /^Резервный слот (\d+)$/.exec(name);
  return (
    (parts?.[1] || "") +
    (reserved ? t("Резервный слот {0}", reserved[1]) : t(name))
  );
}
const fieldEnglish: Record<string, string> = {
  "active-profile": "Active profile",
  "max-profiles": "Available profile count",
  "dpi.stage": "Selected DPI stage",
  "dpi.enabledMask": "Enabled DPI stages",
  "sensor.ripple": "Ripple control",
  "sensor.angleSnap": "Angle snapping",
  "polling.hz": "Polling rate",
  "debounce.ms": "Button debounce",
  "sleep.level1Seconds": "First-stage sleep timeout",
  "sleep.idleMinutes": "Idle timeout",
  "sensor.motionSync": "MotionSync",
  "sensor.lod": "Lift-off distance (LOD)",
  "lighting.mode": "Body lighting",
  "rf.power": "Radio transmit power",
};
export function fieldName(field: SettingField): string {
  if (locale.value === "ru") return field.name;
  const dpi = /^dpi\.(value|color)(\d+)$/.exec(field.id);
  return dpi
    ? dpi[1] === "value"
      ? `DPI: stage ${dpi[2]}`
      : `DPI stage ${dpi[2]} colour`
    : fieldEnglish[field.id] || t(field.name);
}
export function optionName(value: string): string {
  if (locale.value === "en") {
    const profile = /^Профиль ([1-5])$/.exec(value);
    if (profile) return `Profile ${profile[1]}`;
  }
  return locale.value === "en"
    ? value.replace(/Гц/g, "Hz") === value
      ? t(value)
      : value.replace(/Гц/g, "Hz")
    : value;
}
export function uiMessage(value: string): string {
  const entry = Object.entries(english).find(
    ([ru, en]) => value === ru || value === en,
  );
  return entry ? (locale.value === "en" ? entry[1] : entry[0]) : value;
}
export function oledStatus(value: string): string {
  if (locale.value !== "en") return value;
  const phrases: [string, string][] = [
    [
      "Восстановлен последний переданный кадр.",
      "Last submitted frame restored.",
    ],
    ["Восстановление OLED остановлено:", "OLED restore stopped:"],
    ["Кадров отправлено:", "Frames sent:"],
    [
      "Автообновление включено; HUB должен работать.",
      "Live updates enabled; keep the HUB running.",
    ],
    [
      "Сохранённый кадр повреждён; отправьте макет вручную.",
      "The saved frame is damaged; upload the layout manually.",
    ],
  ];
  return phrases.reduce((text, [ru, en]) => text.replace(ru, en), value);
}
export function firmwareName(value: string | null | undefined): string {
  return locale.value === "en"
    ? (value || "").replace(/штатная/g, "stock")
    : value || "";
}
export function unsupportedReason(field: SettingField): string {
  if (locale.value === "ru") return field.unsupportedReason || "";
  if (field.id === "sensor.motionSync")
    return "Hidden in the stock G9/C9 UI; the firmware behaviour for this setting is not verified.";
  if (field.id === "sensor.lod")
    return "Hidden in the stock G9/C9 UI; the exact PAW3311 setting has not been verified.";
  if (field.id === "lighting.mode")
    return "Hidden in the stock G9/C9 UI; existing lighting bytes are preserved.";
  if (field.id === "rf.power")
    return "GET 0x07 reads a different RAM range from SET 0x07. A complete original copy is unavailable.";
  return field.unsupportedReason
    ? t(field.unsupportedReason)
    : "This setting is not available for writing.";
}
