import { t } from "./i18n";
import { computed, reactive, ref } from "vue";
import { call, isDesktop, onState } from "./bridge";
import { emptyState, type AppState } from "./types";
export const state = ref<AppState>(emptyState());
export const working = ref(false);
export const settingsEdits = reactive<Record<string, string>>({});
export const notification = ref<{ message: string; error: boolean } | null>(
  null,
);
export const busy = computed(() => working.value || state.value.app.busy);
let notificationTimer: ReturnType<typeof setTimeout> | undefined;
export function notify(message: string, error = false) {
  notification.value = { message, error };
  clearTimeout(notificationTimer);
  notificationTimer = setTimeout(
    () => (notification.value = null),
    error ? 12_000 : 4500,
  );
}
export function applyState(next: AppState | null | undefined) {
  if (next?.device && next?.screen && next?.app) state.value = next;
}
onState(applyState);
window.addEventListener("g9:locale", () => {
  if (isDesktop)
    void call<AppState>("app.state")
      .then(applyState)
      .catch((error) =>
        notify(error instanceof Error ? error.message : String(error), true),
      );
});
export async function run(
  method: string,
  payload: unknown = {},
  success?: string,
) {
  if (busy.value) {
    notify(t("Дождитесь завершения текущей операции"));
    return null;
  }
  working.value = true;
  try {
    const result = await call<AppState | null>(method, payload);
    applyState(result);
    if (result && success) notify(success);
    return result;
  } catch (error) {
    notify(error instanceof Error ? error.message : String(error), true);
    return null;
  } finally {
    working.value = false;
  }
}
export async function initialize() {
  if (isDesktop) await run("app.state");
}
