<script setup lang="ts">
import { t } from "../i18n";
import { computed, ref, watch } from "vue";
import { state, busy, run, notify } from "../store";
import { isDesktop } from "../bridge";
import { clone, type MacroDocument, type MacroStep } from "../types";
import AppIcon from "./AppIcon.vue";
const draft = ref<MacroDocument>({
  name: t("Новый макрос"),
  repeatMode: "Counted",
  repeatCount: 1,
  steps: [],
});
const originalName = ref(""),
  selectedStep = ref(-1),
  addKind = ref<MacroStep["kind"]>("KeyDown"),
  capture = ref(false),
  dirty = ref(false);
const kinds = computed(() => [
  { id: "KeyDown", name: t("Нажать клавишу") },
  { id: "KeyUp", name: t("Отпустить клавишу") },
  { id: "MouseDown", name: t("Нажать кнопку мыши") },
  { id: "MouseUp", name: t("Отпустить кнопку мыши") },
  { id: "Delay", name: t("Пауза") },
  { id: "Wheel", name: t("Прокрутка") },
  { id: "MouseMove", name: t("Перемещение мыши") },
]);
const buttonNames = computed(() => ({
  Left: t("Левая"),
  Right: t("Правая"),
  Middle: t("Колёсико"),
  Forward: t("Вперёд"),
  Back: t("Назад"),
}));
const activeStep = computed(() => draft.value.steps[selectedStep.value]);
const duration = computed(() =>
  draft.value.steps.reduce(
    (sum, step) => sum + (step.kind === "Delay" ? step.milliseconds : 0),
    0,
  ),
);
let loading = false;
watch(
  draft,
  () => {
    if (!loading) dirty.value = true;
  },
  { deep: true, flush: "sync" },
);
function select(document?: MacroDocument) {
  if (
    dirty.value &&
    !window.confirm(t("Несохранённые изменения будут потеряны. Продолжить?"))
  )
    return;
  loading = true;
  draft.value = clone(
    document || {
      name: t("Новый макрос"),
      repeatMode: "Counted",
      repeatCount: 1,
      steps: [],
    },
  );
  originalName.value = document?.name || "";
  selectedStep.value = -1;
  dirty.value = false;
  loading = false;
}
function newStep(kind: MacroStep["kind"]): MacroStep {
  if (kind === "KeyDown" || kind === "KeyUp") return { kind, virtualKey: 0x30 };
  if (kind === "MouseDown" || kind === "MouseUp")
    return { kind, button: "Left" };
  if (kind === "MouseMove") return { kind, x: 0, y: 0 };
  if (kind === "Wheel") return { kind, delta: 120 };
  return { kind: "Delay", milliseconds: 50 };
}
function add() {
  if (draft.value.steps.length >= 4096) return;
  const at =
    selectedStep.value >= 0 ? selectedStep.value + 1 : draft.value.steps.length;
  draft.value.steps.splice(at, 0, newStep(addKind.value));
  selectedStep.value = at;
}
function removeStep(index: number) {
  draft.value.steps.splice(index, 1);
  selectedStep.value = Math.min(index, draft.value.steps.length - 1);
}
function move(direction: number) {
  const index = selectedStep.value,
    to = index + direction;
  if (index < 0 || to < 0 || to >= draft.value.steps.length) return;
  const item = draft.value.steps.splice(index, 1)[0];
  draft.value.steps.splice(to, 0, item);
  selectedStep.value = to;
}
function keyName(vk: number) {
  if (vk >= 0x30 && vk <= 0x5a) return String.fromCharCode(vk);
  return (
    (
      {
        13: "Enter",
        27: "Esc",
        32: t("Пробел"),
        9: "Tab",
        16: "Shift",
        17: "Ctrl",
        18: "Alt",
      } as Record<number, string>
    )[vk] || `VK ${vk}`
  );
}
function describe(step: MacroStep) {
  switch (step.kind) {
    case "KeyDown":
      return `↓ ${keyName(step.virtualKey)}`;
    case "KeyUp":
      return `↑ ${keyName(step.virtualKey)}`;
    case "MouseDown":
      return `↓ ${buttonNames.value[step.button]}`;
    case "MouseUp":
      return `↑ ${buttonNames.value[step.button]}`;
    case "Delay":
      return t("{0} мс", step.milliseconds);
    case "MouseMove":
      return `X ${step.x}, Y ${step.y}`;
    case "Wheel":
      return `${step.delta > 0 ? "+" : ""}${step.delta}`;
  }
}
function captureKey(event: KeyboardEvent) {
  if (
    !capture.value ||
    !activeStep.value ||
    !("virtualKey" in activeStep.value)
  )
    return;
  event.preventDefault();
  event.stopPropagation();
  activeStep.value.virtualKey = event.keyCode;
  capture.value = false;
}
async function save() {
  if (!draft.value.name.trim()) {
    notify(t("Введите имя макроса"), true);
    return;
  }
  const result = await run(
    "macros.save",
    {
      document: clone(draft.value),
      originalName: originalName.value || undefined,
    },
    t("Макрос сохранён локально"),
  );
  if (result) {
    originalName.value = draft.value.name;
    dirty.value = false;
  }
}
async function removeMacro(name: string) {
  if (!window.confirm(t("Удалить локальный макрос «{0}»?", name))) return;
  const result = await run("macros.delete", { id: name });
  if (result && originalName.value === name) {
    dirty.value = false;
    select();
  }
}
</script>
<template>
  <div class="notice">
    <AppIcon name="info" />
    <p>
      {{
        t(
          "Редактор сохраняет последовательности на компьютере. Загрузка новых макросов в память мыши пока не подключена. В разделе «Назначения» можно выбрать уже записанный аппаратный слот.",
        )
      }}
    </p>
  </div>
  <div class="macro-layout">
    <section class="panel macro-library">
      <div class="section-heading">
        <h2>{{ t("Библиотека") }}</h2>
        <button
          class="icon-button"
          :aria-label="t('Создать макрос')"
          @click="select()"
        >
          <AppIcon name="plus" />
        </button>
      </div>
      <button
        v-for="document in state.macros"
        :key="document.name"
        class="library-row"
        :class="{ selected: originalName === document.name }"
        @click="select(document)"
      >
        <AppIcon name="macro" :size="17" /><span
          ><strong>{{ document.name }}</strong
          ><small>{{ document.steps.length }} {{ t("действий") }}</small></span
        >
      </button>
      <p v-if="!state.macros.length" class="empty-inline">
        {{ t("Создайте первый макрос или импортируйте файл проекта.") }}
      </p>
      <button
        class="button full-width"
        :disabled="busy || !isDesktop"
        @click="run('macros.import')"
      >
        {{ t("Импорт JSON") }}
      </button>
    </section>
    <section class="panel macro-main">
      <div class="section-heading">
        <div>
          <span class="eyebrow">{{ t("ЛОКАЛЬНЫЙ ПРОЕКТ") }}</span>
          <h2>
            {{ draft.name || t("Новый макрос")
            }}<span
              v-if="dirty"
              class="unsaved-dot"
              :title="t('Есть несохранённые изменения')"
            />
          </h2>
        </div>
        <div class="toolbar-group">
          <button
            class="icon-button danger"
            :disabled="busy || !originalName || !isDesktop"
            :aria-label="t('Удалить макрос')"
            @click="removeMacro(originalName)"
          >
            <AppIcon name="trash" :size="18" /></button
          ><button
            class="button"
            :disabled="busy || !originalName || !isDesktop"
            @click="run('macros.export', { id: originalName })"
          >
            {{ t("Экспорт") }}</button
          ><button
            class="button primary"
            :disabled="busy || !isDesktop || !dirty"
            @click="save"
          >
            <AppIcon name="save" :size="16" />{{ t("Сохранить") }}
          </button>
        </div>
      </div>
      <div class="macro-options">
        <label class="field-label"
          >{{ t("Название")
          }}<input
            v-model="draft.name"
            maxlength="20"
            :placeholder="t('До 20 символов')" /></label
        ><label class="field-label"
          >{{ t("Повтор")
          }}<select v-model="draft.repeatMode">
            <option value="Counted">{{ t("Заданное количество") }}</option>
            <option value="Toggle">{{ t("Включение / выключение") }}</option>
            <option value="Hold">{{ t("Пока кнопка удерживается") }}</option>
          </select></label
        ><label v-if="draft.repeatMode === 'Counted'" class="field-label"
          >{{ t("Количество")
          }}<input
            v-model.number="draft.repeatCount"
            type="number"
            min="1"
            max="999"
        /></label>
      </div>
      <div class="add-step">
        <select v-model="addKind" :aria-label="t('Тип нового действия')">
          <option v-for="kind in kinds" :key="kind.id" :value="kind.id">
            {{ kind.name }}
          </option></select
        ><button
          class="button"
          :disabled="draft.steps.length >= 4096"
          @click="add"
        >
          <AppIcon name="plus" :size="15" />{{ t("Добавить") }}</button
        ><span class="hint"
          >{{ draft.steps.length }} {{ t("действий ·") }} {{ duration }}
          {{ t("мс пауз") }}</span
        >
      </div>
      <div class="step-list">
        <div
          v-for="(step, index) in draft.steps"
          :key="index"
          class="step-row"
          :class="{ selected: selectedStep === index }"
        >
          <button
            class="step-select"
            @click="
              selectedStep = index;
              capture = false;
            "
          >
            <span class="step-index">{{
              String(index + 1).padStart(2, "0")
            }}</span><span>{{
              kinds.find((k) => k.id === step.kind)?.name
            }}</span
            ><strong>{{ describe(step) }}</strong></button
          ><button
            class="icon-button"
            :aria-label="t('Удалить действие {0}', index + 1)"
            @click="removeStep(index)"
          >
            ×
          </button>
        </div>
        <div v-if="!draft.steps.length" class="empty-state">
          <AppIcon name="macro" :size="34" />
          <h3>{{ t("Добавьте первое действие") }}</h3>
          <p>
            {{
              t(
                "Для клавиши нужны отдельные нажатие и отпускание. Между ними можно поставить паузу.",
              )
            }}
          </p>
        </div>
      </div>
      <div v-if="activeStep" class="step-editor">
        <label v-if="'virtualKey' in activeStep" class="field-label"
          >{{ t("Код клавиши Windows (VK)") }}
          <div class="toolbar-group">
            <input
              v-model.number="activeStep.virtualKey"
              type="number"
              min="8"
              max="254"
            /><button
              class="button"
              :class="{ primary: capture }"
              @click="capture = true"
              @keydown="captureKey"
              @blur="capture = false"
            >
              {{ capture ? t("Нажмите клавишу…") : t("Выбрать нажатием") }}
            </button>
          </div></label
        ><label v-if="'button' in activeStep" class="field-label"
          >{{ t("Кнопка мыши")
          }}<select v-model="activeStep.button">
            <option v-for="(name, id) in buttonNames" :key="id" :value="id">
              {{ name }}
            </option>
          </select></label
        ><label v-if="activeStep.kind === 'Delay'" class="field-label"
          >{{ t("Длительность паузы, мс")
          }}<input
            v-model.number="activeStep.milliseconds"
            type="number"
            min="1"
            max="99999" /></label
        ><label v-if="activeStep.kind === 'Wheel'" class="field-label"
          >{{ t("Прокрутка (120 = одно деление)")
          }}<input
            v-model.number="activeStep.delta"
            type="number"
            min="-32768"
            max="32767"
        /></label>
        <div v-if="activeStep.kind === 'MouseMove'" class="coordinate-pair">
          <label class="field-label"
            >X<input
              v-model.number="activeStep.x"
              type="number"
              min="-32768"
              max="32767" /></label
          ><label class="field-label"
            >Y<input
              v-model.number="activeStep.y"
              type="number"
              min="-32768"
              max="32767"
          /></label>
        </div>
        <div class="toolbar-group">
          <button
            class="button subtle"
            :disabled="selectedStep <= 0"
            @click="move(-1)"
          >
            {{ t("↑ Выше") }}</button><button
            class="button subtle"
            :disabled="selectedStep === draft.steps.length - 1"
            @click="move(1)"
          >
            {{ t("↓ Ниже") }}
          </button>
        </div>
      </div>
    </section>
  </div>
</template>
