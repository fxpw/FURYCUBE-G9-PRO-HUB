<script setup lang="ts">
import { t, actionName, buttonName } from "../i18n";
import { computed, ref, watch } from "vue";
import { state, busy, run } from "../store";
import { isDesktop } from "../bridge";
import AppIcon from "./AppIcon.vue";
const selected = ref(0),
  actionId = ref(""),
  param1 = ref(0),
  param2 = ref(0),
  showHidden = ref(false),
  search = ref("");
const buttons = computed(() =>
  (state.value.mapping?.buttons || []).filter(
    (button) => showHidden.value || button.visible,
  ),
);
const button = computed(() =>
  state.value.mapping?.buttons.find((item) => item.index === selected.value),
);
const action = computed(() =>
  state.value.bindingActions.find((item) => item.id === actionId.value),
);
const groups = computed(() => [
  ...new Set(state.value.bindingActions.map((item) => item.category)),
]);
const mods = computed(() => [
  t("Левый Ctrl"),
  t("Левый Shift"),
  t("Левый Alt"),
  t("Левый Win"),
  t("Правый Ctrl"),
  t("Правый Shift"),
  t("Правый Alt"),
  t("Правый Win"),
]);
const canApply = computed(
  () =>
    state.value.device.connected &&
    isDesktop &&
    !!action.value &&
    !busy.value &&
    (action.value.parameterKind !== "Keyboard" ||
      param1.value !== 0 ||
      param2.value !== 0),
);
watch(
  [button, () => state.value.mapping],
  () => {
    const current = button.value;
    actionId.value = current?.bindingId || "";
    param1.value = current?.param1 || 0;
    param2.value = current?.param2 || 0;
  },
  { immediate: true },
);
function chooseAction() {
  if (action.value) {
    param1.value = action.value.defaultParam1;
    param2.value = action.value.defaultParam2;
  }
}
function toggleModifier(index: number, enabled: boolean) {
  param1.value = enabled
    ? param1.value | (1 << index)
    : param1.value & ~(1 << index);
}
function bindingName(id: string | null) {
  const found = state.value.bindingActions.find((item) => item.id === id);
  return found ? actionName(found) : t("Неизвестное назначение");
}

const points = [
  { x: 83, y: 107, index: 0, label: "1" },
  { x: 190, y: 107, index: 1, label: "2" },
  { x: 136, y: 137, index: 2, label: "3" },
  { x: 45, y: 240, index: 3, label: "4" },
  { x: 44, y: 191, index: 4, label: "5" },
  { x: 138, y: 234, index: 5, label: "6" },
];
</script>
<template>
  <div class="assignment-layout">
    <section class="panel mouse-panel">
      <div class="section-heading">
        <div>
          <span class="eyebrow">{{ t("КНОПКИ МЫШИ") }}</span>
          <h2>{{ t("Подберите действие") }}</h2>
        </div>
        <span class="tag">G9 PRO</span>
      </div>
      <svg
        class="mouse-art"
        viewBox="0 0 280 430"
        :aria-label="t('Схема кнопок G9 PRO')"
      >
        <defs>
          <linearGradient id="mouse-fill" x2="0.8" y2="1">
            <stop stop-color="#33363b" />
            <stop offset="1" stop-color="#16181c" />
          </linearGradient>
        </defs>
        <ellipse cx="140" cy="380" rx="94" ry="14" fill="#000" opacity=".25" />
        <path
          d="M140 38C67 38 40 85 36 166L31 293c-1 64 34 93 109 93s112-29 109-93l-5-127C239 85 214 38 140 38Z"
          fill="url(#mouse-fill)"
          stroke="#494b50"
          stroke-width="1.5"
        />
        <path
          d="M140 39v127M38 167l78 8m48 0 78-8"
          stroke="#111216"
          stroke-width="3"
        />
        <rect
          x="125"
          y="79"
          width="30"
          height="72"
          rx="13"
          fill="#111216"
          stroke="#484a50"
        />
        <path
          v-for="y in [92, 101, 110, 119, 128, 137]"
          :key="y"
          :d="`M131 ${y}h18`"
          stroke="#6a6c72"
        />
        <rect
          x="107"
          y="205"
          width="65"
          height="45"
          rx="7"
          fill="#0a0c0e"
          stroke="#52545a"
        />
        <text
          x="140"
          y="232"
          text-anchor="middle"
          fill="#9aa0a5"
          font-size="10"
          font-family="monospace"
        >
          OLED
        </text>
        <path
          d="M42 180v30m0 14v30"
          stroke="#63656b"
          stroke-width="6"
          stroke-linecap="round"
        />
        <text
          x="140"
          y="331"
          text-anchor="middle"
          font-size="14"
          fill="#55585e"
          font-family="sans-serif"
          letter-spacing="4"
        >
          FURY
        </text>
        <g
          v-for="point in points"
          :key="point.index"
          class="mouse-point"
          :class="{ chosen: selected === point.index }"
          role="button"
          tabindex="0"
          :aria-label="t('Выбрать кнопку {0}', point.label)"
          @click="selected = point.index"
          @keydown.enter="selected = point.index"
          @keydown.space.prevent="selected = point.index"
        >
          <circle :cx="point.x" :cy="point.y" r="13" />
          <text :x="point.x" :y="point.y + 4" text-anchor="middle">
            {{ point.label }}
          </text>
        </g>
      </svg>
      <p class="hint centered">
        {{ t("Выберите кнопку на схеме или в списке.") }}<br />{{
          t("Кнопка 6 — маленький экран на корпусе.")
        }}
      </p>
    </section>
    <div class="assignment-controls">
      <section class="panel">
        <div class="section-heading">
          <h2>{{ t("Назначения") }}</h2>
          <label class="checkbox small"
            ><input v-model="showHidden" type="checkbox" />{{
              t("Все действия")
            }}</label
          >
        </div>
        <div v-if="!buttons.length" class="empty-state">
          <AppIcon name="mouse" :size="36" />
          <h3>{{ t("Сначала подключите мышь") }}</h3>
          <p>
            {{
              t(
                "После обнаружения здесь появятся назначения, прочитанные из её памяти.",
              )
            }}
          </p>
        </div>
        <div v-else class="button-list">
          <button
            v-for="item in buttons"
            :key="item.index"
            class="button-row"
            :class="{ selected: selected === item.index }"
            @click="selected = item.index"
          >
            <span class="button-number">{{ item.index + 1 }}</span><span
              ><strong>{{ buttonName(item) }}</strong
              ><small>{{ bindingName(item.bindingId) }}</small></span
            ><AppIcon name="arrow" :size="14" />
          </button>
        </div>
      </section>
      <section v-if="button" class="panel binding-editor">
        <div class="section-heading">
          <div>
            <span class="eyebrow">{{ t("РЕДАКТИРОВАНИЕ") }}</span>
            <h2>{{ buttonName(button) }}</h2>
          </div>
          <span class="tag">{{ selected + 1 }}</span>
        </div>
        <label class="field-label"
          >{{ t("Действие")
          }}<select v-model="actionId" @change="chooseAction">
            <option disabled value="">{{ t("Выберите действие") }}</option>
            <optgroup v-for="group in groups" :key="group" :label="t(group)">
              <option
                v-for="item in state.bindingActions.filter(
                  (a) => a.category === group,
                )"
                :key="item.id"
                :value="item.id"
              >
                {{ actionName(item) }}
              </option>
            </optgroup>
          </select></label
        >
        <template v-if="action?.parameterKind === 'Keyboard'"
          ><label class="field-label"
            >{{ t("Клавиша")
            }}<input
              v-model="search"
              :placeholder="t('Поиск клавиши…')"
            /><select v-model="param2">
              <option :value="0">{{ t("Только модификаторы") }}</option>
              <option
                v-for="key in state.keyboardKeys.filter((k) =>
                  t(k.name).toLowerCase().includes(search.toLowerCase()),
                )"
                :key="key.usage"
                :value="key.usage"
              >
                {{ t(key.name) }}
              </option>
            </select></label
          >
          <div class="field-label">
            {{ t("Модификаторы") }}
            <div class="modifier-grid">
              <label
                v-for="(modifier, index) in mods"
                :key="modifier"
                class="checkbox"
                ><input
                  type="checkbox"
                  :checked="Boolean(param1 & (1 << index))"
                  @change="
                    toggleModifier(
                      index,
                      ($event.target as HTMLInputElement).checked,
                    )
                  "
                />{{ modifier }}</label
              >
            </div>
          </div></template
        >
        <label v-else-if="action?.parameterKind === 'Macro'" class="field-label"
          >{{ t("Уже записанный слот в мыши")
          }}<select v-model="param2">
            <option v-for="slot in 32" :key="slot" :value="slot">
              {{ t("Слот") }} {{ slot
              }}{{ slot === 13 ? t("· штатный для кнопки-экрана") : "" }}
            </option></select
          ><small class="hint">{{
            t(
              "Назначает макрос, который уже находится в памяти. Локальный редактор последовательностей не загружает его в мышь.",
            )
          }}</small></label
        >
        <label
          v-else-if="action?.parameterKind === 'Sniper'"
          class="field-label"
          >{{ t("Уровень DPI при удержании")
          }}<select v-model="param2">
            <option v-for="stage in 8" :key="stage" :value="stage">
              {{ t("Уровень") }} {{ stage }}
            </option>
          </select></label
        >
        <label v-else-if="action?.parameterKind === 'Fire'" class="field-label"
          >{{ t("Параметр скоростного клика")
          }}<input v-model.number="param2" type="number" min="0" max="255"
        /></label>
        <div class="action-bar">
          <span class="hint"
            >{{ t("Запись в профиль") }}
            {{ state.mapping?.profile || state.device.profile }}</span
          ><button
            class="button primary"
            :disabled="!canApply"
            @click="
              run(
                'device.assign',
                {
                  profile: state.mapping?.profile || state.device.profile,
                  index: selected,
                  actionId,
                  param1,
                  param2,
                },
                t('Назначение записано и проверено'),
              )
            "
          >
            <AppIcon name="check" :size="16" />{{ t("Применить") }}
          </button>
        </div>
      </section>
    </div>
  </div>
</template>
