<script setup lang="ts">
import { t, locale } from "../i18n";
import { ref } from "vue";
import { state, busy, run } from "../store";
import { isDesktop } from "../bridge";
import AppIcon from "./AppIcon.vue";
const showLogs = ref(false);
async function preferenceChanged(
  event: Event,
  key: "autostart" | "resumeOled",
) {
  const input = event.target as HTMLInputElement;
  await run("app.preferences", { [key]: input.checked });
  input.checked = state.value.app[key];
}
async function copyLogs() {
  try {
    await navigator.clipboard.writeText(state.value.logs.join("\n"));
  } catch {
    /* Logs remain selectable if clipboard permission is unavailable. */
  }
}
</script>
<template>
  <section class="panel preferences-panel">
    <div class="section-heading">
      <div>
        <span class="eyebrow">{{ t("ПОВЕДЕНИЕ ПРИЛОЖЕНИЯ") }}</span>
        <h2>{{ t("Всегда под рукой") }}</h2>
      </div>
      <AppIcon name="settings" />
    </div>
    <label class="preference-row language-preference"
      ><span
        ><strong>{{ t("Язык интерфейса") }}</strong
        ><small>{{
          t(
            "Выберите язык приложения. Названия профилей, макросов и текст на экране не меняются.",
          )
        }}</small></span
      ><select v-model="locale" :aria-label="t('Язык интерфейса')">
        <option value="ru">Русский</option>
        <option value="en">English</option>
      </select></label
    >
    <label class="preference-row"
      ><span
        ><strong>{{ t("Запускать HUB вместе с Windows") }}</strong
        ><small>{{
          t("Приложение будет запускаться автоматически после входа.")
        }}</small></span
      ><input
        class="switch"
        type="checkbox"
        :checked="state.app.autostart"
        :disabled="busy || !isDesktop"
        @change="preferenceChanged($event, 'autostart')" /></label
    ><label class="preference-row"
      ><span
        ><strong>{{ t("Продолжать обновление OLED при запуске") }}</strong
        ><small>{{
          t("Возобновлять виджеты, когда поддерживаемая мышь подключена.")
        }}</small></span
      ><input
        class="switch"
        type="checkbox"
        :checked="state.app.resumeOled"
        :disabled="busy || !isDesktop"
        @change="preferenceChanged($event, 'resumeOled')"
    /></label>
    <div class="preference-row">
      <span
        ><strong>{{ t("Прочитать мышь заново") }}</strong
        ><small>{{
          t("Обновить соединение, прошивку, назначения и доступные настройки.")
        }}</small></span
      ><button
        class="button"
        :disabled="busy || !isDesktop"
        @click="run('device.scan')"
      >
        <AppIcon name="refresh" :size="16" />{{ t("Обновить") }}
      </button>
    </div>
  </section>
  <section class="panel">
    <div class="section-heading">
      <div>
        <h2>{{ t("Журнал работы") }}</h2>
        <p class="hint">
          {{ t("Сообщения приложения и устройства для диагностики.") }}
        </p>
      </div>
      <div class="toolbar-group">
        <button
          class="button subtle"
          :disabled="!state.logs.length"
          @click="copyLogs"
        >
          {{ t("Копировать") }}</button><button
          class="button"
          @click="showLogs = !showLogs"
        >
          {{ showLogs ? t("Свернуть") : t("Показать журнал") }}
        </button>
      </div>
    </div>
    <p v-if="locale === 'en'" class="hint source-language-note">
      {{
        t(
          "Технические сообщения устройства и Windows показаны на языке источника.",
        )
      }}
    </p>
    <pre v-if="showLogs" class="log-view" tabindex="0">{{
      state.logs.length ? state.logs.join("\n") : t("Журнал пока пуст.")
    }}</pre>
  </section>
  <section class="panel about-panel">
    <div class="brand">
      <span class="brand-symbol">F</span>
      <div>G9 PRO HUB<small>FURYCUBE · EXPERIMENTAL</small></div>
    </div>
    <p>
      {{
        t(
          "Настройка мыши и редактор OLED. Аппаратные возможности зависят от установленной прошивки и способа подключения.",
        )
      }}
    </p>
    <button
      class="button danger"
      :disabled="busy || !isDesktop"
      @click="run('app.exit')"
    >
      {{ t("Завершить HUB") }}
    </button>
  </section>
</template>
