export interface BindingAction {
  id: string;
  name: string;
  category: string;
  function: number;
  defaultParam1: number;
  defaultParam2: number;
  parameterKind: string;
}
export interface ButtonMapping {
  index: number;
  name: string;
  physicalLabel: string;
  visible: boolean;
  function: number;
  param1: number;
  param2: number;
  bindingId: string | null;
}
export interface SettingField {
  id: string;
  name: string;
  type: string;
  value: string | null;
  unit: string;
  min: number | null;
  max: number | null;
  step: number | null;
  options: { value: string; name: string }[];
  canWrite: boolean;
  unsupportedReason?: string;
}
export interface ScreenWidget {
  Id: string;
  Kind: number;
  X: number;
  Y: number;
  Width: number;
  Height: number;
  Text: string;
  Format: string;
  FontSize: number;
  Bold: boolean;
  Lit: boolean;
  ClearBackground: boolean;
  ShowLabel: boolean;
}
export interface ScreenProject {
  Version: number;
  Width: number;
  Height: number;
  CultureName: string;
  ImageFile: string | null;
  ImageX: number;
  ImageY: number;
  ImageWidth: number;
  ImageHeight: number;
  ImageLayerIndex?: number;
  ImageOptions: {
    Resize: number;
    Threshold: number;
    Dither: boolean;
    Invert: boolean;
    BackgroundLit: boolean;
  };
  Composition: number;
  Widgets: ScreenWidget[];
}
export interface ScreenState {
  project: ScreenProject;
  pngBase64: string;
  status: string;
  live: boolean;
  widgetNames: string[];
}
export type MacroStep =
  | { kind: "KeyDown" | "KeyUp"; virtualKey: number }
  | {
      kind: "MouseDown" | "MouseUp";
      button: "Left" | "Right" | "Middle" | "Forward" | "Back";
    }
  | { kind: "MouseMove"; x: number; y: number }
  | { kind: "Wheel"; delta: number }
  | { kind: "Delay"; milliseconds: number };
export interface MacroDocument {
  name: string;
  repeatMode: "Counted" | "Toggle" | "Hold";
  repeatCount: number;
  steps: MacroStep[];
}
export interface ProfileSummary {
  id: string;
  name: string;
  updatedAt?: string;
  createdAt?: string;
}
export interface AppState {
  app: { autostart: boolean; resumeOled: boolean; busy: boolean };
  device: {
    connected: boolean;
    connection: "usb" | "radio" | "none";
    name: string;
    firmware: string;
    profile: number;
    settingsAvailable: boolean;
    error?: string;
    lastReadAt?: string;
  };
  mapping: {
    profile: number;
    buttons: ButtonMapping[];
    keymapHex?: string;
  } | null;
  settings: {
    profile: number;
    fields: SettingField[];
    readAtUtc?: string;
    activeProfile?: number;
    maxProfiles?: number;
  } | null;
  profileDraft?: {
    schemaVersion: number;
    name: string;
    values: Record<string, string>;
    keymapHex?: string;
  } | null;
  bindingActions: BindingAction[];
  keyboardKeys: { name: string; usage: number }[];
  screen: ScreenState;
  macros: MacroDocument[];
  profiles: ProfileSummary[];
  logs: string[];
}
export const widgetLabels = [
  "Текст",
  "Часы",
  "Заряд батареи",
  "DPI",
  "Загрузка CPU",
  "Загрузка RAM",
  "Загрузка GPU",
  "Температура CPU",
  "Температура GPU",
  "Уровень DPI",
  "Индикатор батареи",
];
export function newWidget(kind = 0): ScreenWidget {
  return {
    Id: crypto.randomUUID().replaceAll("-", ""),
    Kind: kind,
    X: 0,
    Y: 0,
    Width: 60,
    Height: 12,
    Text: "G9 PRO",
    Format: "HH:mm",
    FontSize: 9,
    Bold: false,
    Lit: true,
    ClearBackground: true,
    ShowLabel: true,
  };
}
export function emptyProject(): ScreenProject {
  return {
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
    Widgets: [newWidget(1)],
  };
}
export function emptyState(): AppState {
  return {
    app: { autostart: false, resumeOled: false, busy: false },
    device: {
      connected: false,
      connection: "none",
      name: "G9 PRO",
      firmware: "",
      profile: 1,
      settingsAvailable: false,
    },
    mapping: null,
    settings: null,
    bindingActions: [],
    keyboardKeys: [],
    screen: {
      project: emptyProject(),
      pngBase64: "",
      status: "",
      live: false,
      widgetNames: widgetLabels,
    },
    macros: [],
    profiles: [],
    logs: [],
  };
}
export function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}
