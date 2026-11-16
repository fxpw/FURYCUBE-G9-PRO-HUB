<script setup lang="ts">
import { t, locale, localeTag, firmwareName, uiMessage } from "./i18n";
import { computed, onMounted, ref } from "vue";
import { isDesktop } from "./bridge";
import { busy, initialize, notification, run, state } from "./store";
import AppIcon from "./components/AppIcon.vue";
import AssignmentsView from "./components/AssignmentsView.vue";
import DeviceSettingsView from "./components/DeviceSettingsView.vue";
import ScreenEditor from "./components/ScreenEditor.vue";
import MacroEditor from "./components/MacroEditor.vue";
import ProfilesView from "./components/ProfilesView.vue";
import PreferencesView from "./components/PreferencesView.vue";
const page = ref("assignments");
const views: Record<string, unknown> = {
  assignments: AssignmentsView,
  device: DeviceSettingsView,
  screen: ScreenEditor,
  macros: MacroEditor,
  profiles: ProfilesView,
  preferences: PreferencesView,
};
const pages = computed(() => [
  {
    id: "assignments",
    label: t("Назначения"),
    group: t("Мышь"),
    icon: "mouse",
    subtitle: t("Каждая кнопка — по вашему сценарию"),
  },
  {
    id: "device",
    label: t("DPI и сенсор"),
    group: "",
    icon: "settings",
    subtitle: t("Чувствительность, отклик и питание"),
  },
  {
    id: "screen",
    label: t("Экран OLED"),
    group: t("Рабочее пространство"),
    icon: "screen",
    subtitle: t("Ваш экран. Ваши виджеты."),
  },
  {
    id: "macros",
    label: t("Макросы"),
    group: "",
    icon: "macro",
    subtitle: t("Последовательности действий"),
  },
  {
    id: "profiles",
    label: t("Профили"),
    group: "",
    icon: "profiles",
    subtitle: t("Сохранённые настройки для разных задач"),
  },
  {
    id: "preferences",
    label: t("Настройки"),
    group: t("Приложение"),
    icon: "settings",
    subtitle: t("Запуск и поведение HUB"),
  },
]);
const componentEvents = computed(() =>
  page.value === "profiles" ? { settings: () => (page.value = "device") } : {},
);
const active = computed(() =>
  pages.value.find((item) => item.id === page.value)!,
);
const connection = computed(() =>
  !state.value.device.connected
    ? t("Не подключена")
    : state.value.device.connection === "radio"
      ? t("Радио · 2.4 ГГц")
      : t("USB · кабель"),
);
onMounted(initialize);
</script>
<template>
  <div class="app-shell">
    <aside class="sidebar">
      <div class="brand">
        <span class="brand-symbol">F</span>
        <div>FURYCUBE<small>CONTROL HUB</small></div>
      </div>
      <div class="device-card">
        <span class="device-glyph"><AppIcon name="mouse" :size="30" /></span>
        <div>
          <strong>G9 PRO</strong
          ><span class="device-status"
            ><i :class="{ online: state.device.connected }" />{{
              connection
            }}</span
          >
        </div>
      </div>
      <nav :aria-label="t('Разделы HUB')">
        <template v-for="item in pages" :key="item.id"
          ><p v-if="item.group" class="nav-group">{{ item.group }}</p>
          <button
            class="nav-item"
            :class="{ active: page === item.id }"
            :aria-current="page === item.id ? 'page' : undefined"
            @click="page = item.id"
          >
            <AppIcon :name="item.icon" /><span>{{ item.label }}</span
            ><AppIcon v-if="page === item.id" name="arrow" :size="13" /></button
        ></template>
      </nav>
      <div class="sidebar-bottom">
        <label class="language-control"
          ><span>{{ t("Язык интерфейса") }}</span
          ><select v-model="locale" :aria-label="t('Язык интерфейса')">
            <option value="ru">Русский</option>
            <option value="en">English</option>
          </select></label
        >
        <div class="connection-meta">
          <i :class="{ online: state.device.connected }" /><span>{{
            firmwareName(state.device.firmware) || t("Ожидание устройства")
          }}</span>
        </div>
        <small>{{
          isDesktop
            ? t("Настольное приложение")
            : t("Браузер · редактор без устройства")
        }}</small><span class="build-label"
          >G9 PRO HUB <b>EXPERIMENTAL</b></span
        >
      </div>
    </aside>
    <main class="main-shell">
      <header class="topbar">
        <div>
          <span class="eyebrow">G9 PRO / {{ active.label }}</span>
          <h1>{{ active.label }}</h1>
          <p>{{ active.subtitle }}</p>
        </div>
        <div class="topbar-actions">
          <span v-if="state.device.connected" class="chip"
            >{{ t("Профиль") }} {{ state.device.profile }}</span
          ><button
            class="button subtle"
            :disabled="busy || !isDesktop"
            @click="run('device.scan')"
          >
            <AppIcon name="refresh" :size="16" :class="{ spinning: busy }" />{{
              busy ? t("Подождите…") : t("Найти мышь")
            }}
          </button>
        </div>
      </header>
      <div class="main-content">
        <div v-if="!isDesktop" class="notice demo-notice">
          <AppIcon name="info" />
          <div>
            <strong>{{ t("Предпросмотр интерфейса в браузере") }}</strong>
            <p>
              {{
                t(
                  "Подключение мыши, системные показатели и запись на OLED доступны в настольном HUB. Здесь можно редактировать макет без отправки на устройство.",
                )
              }}
            </p>
          </div>
        </div>
        <div v-else-if="state.device.error" class="notice warning">
          <AppIcon name="info" />
          <div>
            <strong>{{ t("Устройство требует внимания") }}</strong>
            <p>{{ state.device.error }}</p>
          </div>
        </div>
        <KeepAlive
          ><component :is="views[page]" v-on="componentEvents"
        /></KeepAlive>
      </div>
      <footer class="app-footer">
        <span
          ><i :class="{ online: state.device.connected }" />{{
            connection
          }}</span
        ><span v-if="state.device.lastReadAt"
          >{{ t("Прочитано:") }}
          {{
            new Date(state.device.lastReadAt).toLocaleTimeString(localeTag)
          }}</span
        ><span>FURYCUBE G9 PRO</span>
      </footer>
    </main>
    <Transition name="toast"
      ><div
        v-if="notification"
        class="toast"
        :class="{ error: notification.error }"
        role="status"
      >
        <AppIcon :name="notification.error ? 'info' : 'check'" /><span
          ><strong v-if="notification.error" class="error-title">{{
            t("Системное сообщение")
          }}</strong
          >{{
            notification.error
              ? notification.message
              : uiMessage(notification.message)
          }}</span
        ><button
          :aria-label="t('Закрыть уведомление')"
          @click="notification = null"
        >
          ×
        </button>
      </div></Transition
    >
  </div>
</template>
