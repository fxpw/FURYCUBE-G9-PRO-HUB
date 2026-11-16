<script setup lang="ts">
import { t, fieldName, unsupportedReason, optionName } from "../i18n";
import { computed, ref, watch } from "vue";
import { state, busy, run, notify, settingsEdits } from "../store";
import { isDesktop } from "../bridge";
import type { SettingField } from "../types";
import AppIcon from "./AppIcon.vue";
import RangeField from "./RangeField.vue";
const tab = ref("dpi"),
  edits = settingsEdits,
  includeButtons = ref(false);
const fields = computed(() => state.value.settings?.fields || []);
// Native state events recreate DTO objects every second. Only a genuinely new
// readback may reset pending edits; object identity is not a device generation.
watch(
  () => {
    const current = state.value.settings;
    return current
      ? `${current.profile}:${current.readAtUtc || JSON.stringify(current.fields.map((f) => [f.id, f.value]))}`
      : "none";
  },
  () => {
    Object.keys(edits).forEach((key) => delete edits[key]);
    includeButtons.value = false;
  },
);
watch(
  () => JSON.stringify(state.value.profileDraft),
  () => {
    const draft = state.value.profileDraft;
    if (!draft) return;
    Object.keys(edits).forEach((key) => delete edits[key]);
    for (const field of fields.value) {
      const v = draft.values[field.id];
      if (field.canWrite && v !== undefined && String(v) !== field.value)
        edits[field.id] = String(v);
    }
    includeButtons.value = false;
  },
  { immediate: true },
);
const tabs = computed(() => [
  { id: "dpi", name: t("Уровни DPI") },
  { id: "sensor", name: t("Сенсор и отклик") },
  { id: "power", name: t("Питание") },
  { id: "other", name: t("Профиль и прочее") },
]);
function value(field: SettingField) {
  return edits[field.id] ?? field.value ?? "";
}
function set(field: SettingField, val: string | number) {
  if (String(val) === field.value) delete edits[field.id];
  else edits[field.id] = String(val);
}
function matches(field: SettingField) {
  if (tab.value === "dpi") return field.id.startsWith("dpi.");
  if (tab.value === "sensor")
    return /^(sensor\.|polling\.|debounce\.)/.test(field.id);
  if (tab.value === "power") return field.id.startsWith("sleep.");
  return !/^(dpi\.|sensor\.|polling\.|debounce\.|sleep\.)/.test(field.id);
}
const visible = computed(() => fields.value.filter(matches));
const writable = computed(
  () =>
    state.value.device.connected &&
    state.value.device.settingsAvailable &&
    isDesktop &&
    !busy.value,
);
const changed = computed(() => Object.keys(edits).length);
function toggleStage(field: SettingField, index: number, enabled: boolean) {
  const mask = Number(value(field));
  const next = enabled ? mask | (1 << index) : mask & ~(1 << index);
  if (!next) {
    notify(t("Оставьте хотя бы один уровень DPI"), true);
    return;
  }
  set(field, next);
}
async function apply() {
  const changes = Object.entries(edits).map(([id, v]) => ({ id, value: v }));
  const invalid = changes.find(
    (item) =>
      item.id.startsWith("dpi.value") &&
      Number(item.value) % (Number(item.value) > 10000 ? 100 : 50),
  );
  if (invalid) {
    notify(t("Шаг DPI: 50 до 10 000, затем 100"), true);
    return;
  }
  if (
    changes.some((c) => ["active-profile", "max-profiles"].includes(c.id)) &&
    (changes.length > 1 || includeButtons.value)
  ) {
    notify(
      t(
        "Изменение активного профиля или количества профилей примените отдельно кнопкой рядом с полем.",
      ),
      true,
    );
    return;
  }
  const result = await run(
    "device.settings",
    {
      profile: state.value.settings?.profile || state.value.device.profile,
      changes,
      includeButtons: includeButtons.value,
    },
    t("Настройки прочитаны после записи"),
  );
  if (result) {
    Object.keys(edits).forEach((key) => delete edits[key]);
    includeButtons.value = false;
  }
}
async function applyGlobal(field: SettingField) {
  const result = await run(
    "device.settings",
    {
      profile: state.value.settings?.profile || state.value.device.profile,
      changes: [{ id: field.id, value: value(field) }],
    },
    t("Настройка профиля применена"),
  );
  if (result) delete edits[field.id];
}
</script>
<template>
  <div v-if="state.profileDraft" class="notice">
    <AppIcon name="profiles" />
    <div>
      <strong>{{ t("Черновик:") }} {{ state.profileDraft.name }}</strong>
      <p>
        {{
          t(
            "Изменения показаны поверх текущих настроек. Нажмите «Применить», чтобы записать выбранные значения.",
          )
        }}
      </p>
      <label
        v-if="state.profileDraft.keymapHex"
        class="checkbox"
        style="margin-top: 9px"
        ><input
          v-model="includeButtons"
          type="checkbox"
          :disabled="!writable"
        />{{ t("Также применить назначения кнопок из профиля") }}</label
      >
    </div>
  </div>
  <div class="tabbar" role="tablist" :aria-label="t('Настройки мыши')">
    <button
      v-for="item in tabs"
      :key="item.id"
      :class="{ active: tab === item.id }"
      role="tab"
      :aria-selected="tab === item.id"
      @click="tab = item.id"
    >
      {{ item.name }}
    </button>
  </div>
  <div v-if="state.device.connection === 'radio'" class="notice">
    <AppIcon name="info" />
    <p>
      {{
        t(
          "По 2.4G доступны назначения, DPI, сенсор и частота опроса. Для таймеров питания и debounce подключите USB-кабель.",
        )
      }}
    </p>
  </div>
  <section v-if="!fields.length" class="panel empty-state">
    <AppIcon name="settings" :size="38" />
    <h2>{{ t("Настройки ещё не прочитаны") }}</h2>
    <p>
      {{
        t(
          "Подключите G9 PRO по USB и нажмите «Найти мышь». Значения появятся после чтения устройства.",
        )
      }}
    </p>
    <button
      class="button"
      :disabled="busy || !isDesktop"
      @click="run('device.scan')"
    >
      {{ t("Прочитать устройство") }}
    </button>
  </section>
  <section v-else class="panel settings-panel">
    <div class="section-heading">
      <div>
        <h2>{{ tabs.find((t) => t.id === tab)?.name }}</h2>
        <p class="hint">
          {{ t("Значения из памяти мыши · профиль") }}
          {{ state.settings?.profile }}
        </p>
      </div>
      <span class="tag">{{
        changed ? t("{0} изменений", changed) : t("Прочитано")
      }}</span>
    </div>
    <div class="settings-grid">
      <div
        v-for="field in visible"
        :key="field.id"
        class="setting-item"
        :class="{ unavailable: !field.canWrite }"
      >
        <div v-if="!field.canWrite && field.value === null">
          <strong class="field-label">{{ fieldName(field) }}</strong>
          <span class="hint">—</span>
        </div>
        <template v-else-if="field.id === 'dpi.enabledMask'"
          ><span class="field-label">{{ t("Включённые уровни DPI") }}</span>
          <div class="stage-toggles">
            <label
              v-for="index in 8"
              :key="index"
              :class="{ checked: Number(value(field)) & (1 << (index - 1)) }"
              ><input
                type="checkbox"
                :checked="Boolean(Number(value(field)) & (1 << (index - 1)))"
                :disabled="!writable || !field.canWrite"
                @change="
                  toggleStage(
                    field,
                    index - 1,
                    ($event.target as HTMLInputElement).checked,
                  )
                "
              />{{ index }}</label
            >
          </div>
          <p class="hint">
            {{ t("Активный уровень должен оставаться включённым.") }}
          </p></template
        >
        <label v-else-if="field.type === 'color'" class="field-label"
          >{{ fieldName(field) }}
          <div class="color-control">
            <input
              type="color"
              :value="'#' + (value(field) || 'ffffff')"
              :disabled="!writable || !field.canWrite"
              @input="
                set(
                  field,
                  ($event.target as HTMLInputElement).value
                    .slice(1)
                    .toUpperCase(),
                )
              "
            /><span>#{{ value(field) }}</span>
          </div></label
        >
        <label
          v-else-if="
            field.options?.length === 2 &&
            field.options.every((o) => ['0', '1'].includes(o.value))
          "
          class="toggle-row"
          ><span>{{ fieldName(field) }}</span
          ><input
            type="checkbox"
            class="switch"
            :checked="value(field) === '1'"
            :disabled="!writable || !field.canWrite"
            @change="
              set(
                field,
                ($event.target as HTMLInputElement).checked ? '1' : '0',
              )
            "
        /></label>
        <label v-else-if="field.options?.length" class="field-label"
          >{{ fieldName(field)
          }}<select
            :value="value(field)"
            :disabled="!writable || !field.canWrite"
            @change="set(field, ($event.target as HTMLSelectElement).value)"
          >
            <option
              v-for="option in field.options"
              :key="option.value"
              :value="option.value"
            >
              {{ optionName(option.name) }}
            </option>
          </select></label
        >
        <RangeField
          v-else-if="
            field.min !== null && field.max !== null && field.max > field.min
          "
          :label="fieldName(field)"
          :model-value="Number(value(field))"
          :min="field.min"
          :max="field.max"
          :step="field.step || 1"
          :unit="t(field.unit)"
          :disabled="!writable || !field.canWrite"
          @update:model-value="set(field, $event)"
        />
        <div v-else>
          <strong class="field-label">{{ fieldName(field) }}</strong><span
            class="hint"
            >{{ field.value || t("Не поддерживается") }}</span
          >
        </div>
        <button
          v-if="
            ['active-profile', 'max-profiles'].includes(field.id) &&
            edits[field.id] !== undefined
          "
          class="button subtle"
          :disabled="!writable"
          @click="applyGlobal(field)"
        >
          {{ t("Применить отдельно") }}
        </button>
        <p v-if="!field.canWrite && field.unsupportedReason" class="hint">
          {{ unsupportedReason(field) }}
        </p>
        <p v-if="field.type === 'dpi'" class="hint">
          {{ t("Шаг 50 до 10 000 DPI, затем 100 DPI.") }}
        </p>
      </div>
    </div>
    <div class="action-bar">
      <button
        class="button subtle"
        :disabled="!changed"
        @click="Object.keys(edits).forEach((key) => delete edits[key])"
      >
        {{ t("Отменить изменения") }}</button><button
        class="button primary"
        :disabled="(!changed && !includeButtons) || !writable"
        @click="apply"
      >
        <AppIcon name="check" :size="16" />{{ t("Применить") }}
        {{ changed || "" }}
      </button>
    </div>
  </section>
</template>
