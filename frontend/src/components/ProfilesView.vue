<script setup lang="ts">
import { t, localeTag } from "../i18n";
import { ref } from "vue";
import { state, busy, run, notify, settingsEdits } from "../store";
import { isDesktop } from "../bridge";
import type { ProfileSummary } from "../types";
import AppIcon from "./AppIcon.vue";
const name = ref("");
const emit = defineEmits<{ settings: [] }>();
async function save() {
  if (!name.value.trim()) {
    notify(t("Введите название профиля"), true);
    return;
  }
  const values = Object.fromEntries(
    (state.value.settings?.fields || [])
      .filter(
        (field) =>
          field.canWrite &&
          field.value !== null &&
          !["active-profile", "max-profiles"].includes(field.id),
      )
      .map((field) => [field.id, settingsEdits[field.id] ?? field.value]),
  );
  const result = await run(
    "profiles.save",
    { name: name.value.trim(), values },
    t("Профиль сохранён"),
  );
  if (result) name.value = "";
}
async function open(profile: ProfileSummary) {
  const result = await run(
    "profiles.open",
    { id: profile.id },
    t("Профиль открыт как черновик"),
  );
  if (result) emit("settings");
}
async function rename(profile: ProfileSummary) {
  const value = window.prompt(t("Название профиля"), profile.name);
  if (value?.trim() && value.trim() !== profile.name)
    await run("profiles.rename", { id: profile.id, name: value.trim() });
}
async function remove(profile: ProfileSummary) {
  if (
    window.confirm(
      t(
        "Удалить сохранённый профиль «{0}»? Настройки мыши сохранятся.",
        profile.name,
      ),
    )
  )
    await run("profiles.delete", { id: profile.id });
}
</script>
<template>
  <section class="panel profile-intro">
    <div>
      <span class="eyebrow">{{ t("ЛОКАЛЬНАЯ БИБЛИОТЕКА") }}</span>
      <h2>{{ t("Под разные задачи") }}</h2>
      <p>
        {{
          t(
            "Сохраняйте прочитанные настройки мыши и переносите профили через JSON. Открытый профиль сначала становится черновиком для проверки.",
          )
        }}
      </p>
    </div>
    <AppIcon name="profiles" :size="62" />
  </section>
  <section class="panel">
    <div class="section-heading">
      <h2>{{ t("Сохранить текущие настройки") }}</h2>
      <span class="tag">{{
        state.device.settingsAvailable
          ? t("Настройки прочитаны")
          : t("Нужны данные устройства")
      }}</span>
    </div>
    <form class="save-profile" @submit.prevent="save">
      <label class="field-label"
        >{{ t("Название профиля")
        }}<input
          v-model="name"
          maxlength="80"
          :placeholder="t('Например, работа или игры')" /></label
      ><button
        type="submit"
        class="button primary"
        :disabled="busy || !isDesktop || !state.device.settingsAvailable"
      >
        <AppIcon name="save" :size="16" />{{ t("Сохранить профиль") }}
      </button>
    </form>
  </section>
  <section class="panel">
    <div class="section-heading">
      <h2>
        {{ t("Мои профили") }}
        <span class="inline-count">{{ state.profiles.length }}</span>
      </h2>
      <button
        class="button"
        :disabled="busy || !isDesktop"
        @click="run('profiles.import')"
      >
        {{ t("Импорт JSON") }}
      </button>
    </div>
    <div v-if="!state.profiles.length" class="empty-state">
      <AppIcon name="profiles" :size="34" />
      <h3>{{ t("Здесь будут ваши профили") }}</h3>
      <p>
        {{ t("Прочитайте мышь по USB и сохраните первый набор настроек.") }}
      </p>
    </div>
    <div v-else class="profile-grid">
      <article
        v-for="profile in state.profiles"
        :key="profile.id"
        class="profile-tile"
      >
        <div class="profile-tile-heading">
          <span class="profile-avatar"
            ><AppIcon name="profiles" :size="25"
          /></span>
          <div>
            <h3>{{ profile.name }}</h3>
            <small v-if="profile.updatedAt">{{
              new Date(profile.updatedAt).toLocaleString(localeTag)
            }}</small>
          </div>
        </div>
        <button
          class="button primary full-width"
          :disabled="busy || !isDesktop"
          @click="open(profile)"
        >
          {{ t("Открыть") }}
        </button>
        <div class="profile-tile-actions">
          <button :disabled="busy" @click="rename(profile)">
            {{ t("Переименовать") }}</button><button
            :disabled="busy"
            @click="run('profiles.export', { id: profile.id })"
          >
            {{ t("Экспорт") }}</button
          ><button class="danger" :disabled="busy" @click="remove(profile)">
            {{ t("Удалить") }}
          </button>
        </div>
      </article>
    </div>
  </section>
</template>
