import { t, locale } from "./i18n";
import type { AppState } from "./types";
interface NativeWebView {
  postMessage(message: unknown): void;
  addEventListener(
    type: "message",
    listener: (event: MessageEvent) => void,
  ): void;
  removeEventListener(
    type: "message",
    listener: (event: MessageEvent) => void,
  ): void;
}
declare global {
  interface Window {
    chrome?: { webview?: NativeWebView };
  }
}
export const isDesktop = Boolean(window.chrome?.webview);
type Pending = {
  resolve(value: unknown): void;
  reject(reason: Error): void;
  timer: ReturnType<typeof setTimeout>;
};
const pending = new Map<string, Pending>();
const listeners = new Set<(state: AppState) => void>();
let counter = 0;
window.chrome?.webview?.addEventListener("message", (event) => {
  const message =
    typeof event.data === "string" ? parse(event.data) : event.data;
  if (!message || typeof message !== "object") return;
  if (message.event === "state" && message.data) {
    listeners.forEach((listener) => listener(message.data as AppState));
    return;
  }
  const entry = pending.get(String(message.id));
  if (!entry) return;
  clearTimeout(entry.timer);
  pending.delete(String(message.id));
  if (message.ok) entry.resolve(message.data);
  else
    entry.reject(
      new Error(
        typeof message.error === "string"
          ? message.error
          : message.error?.message || t("Приложение не выполнило операцию"),
      ),
    );
});
function parse(value: string) {
  try {
    return JSON.parse(value);
  } catch {
    return null;
  }
}
export function onState(listener: (state: AppState) => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
export function call<T>(method: string, payload: unknown = {}): Promise<T> {
  if (!window.chrome?.webview)
    return Promise.reject(
      new Error(
        t(
          "Эта операция доступна в настольном HUB. В браузере открыт только редактор.",
        ),
      ),
    );
  const id = `g9-${++counter}`;
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(
        new Error(
          t(
            "Приложение не ответило за 45 секунд. Проверьте соединение и журнал.",
          ),
        ),
      );
    }, 45_000);
    pending.set(id, { resolve: (value) => resolve(value as T), reject, timer });
    try {
      window.chrome!.webview!.postMessage({
        id,
        method,
        payload: {
          ...(payload as Record<string, unknown>),
          locale: locale.value,
        },
      });
    } catch (error) {
      clearTimeout(timer);
      pending.delete(id);
      reject(error);
    }
  });
}
window.addEventListener("beforeunload", () => {
  pending.forEach((entry) => {
    clearTimeout(entry.timer);
    entry.reject(new Error(t("Окно закрыто")));
  });
  pending.clear();
});
