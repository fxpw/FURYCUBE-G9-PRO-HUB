<script setup lang="ts">
import { t, locale, firmwareName, oledStatus } from "../i18n";
import {
  computed,
  nextTick,
  onBeforeUnmount,
  onDeactivated,
  ref,
  watch,
} from "vue";
import { call, isDesktop } from "../bridge";
import { busy, notify, run, state } from "../store";
import {
  clone,
  newWidget,
  widgetLabels,
  type ScreenProject,
  type ScreenState,
  type ScreenWidget,
} from "../types";
import AppIcon from "./AppIcon.vue";
import RangeField from "./RangeField.vue";
const project = ref<ScreenProject>(clone(state.value.screen.project));
const batteryFullWidth = 113;
const batteryIndicatorKind = 10;
const selected = ref(project.value.Widgets[0]?.Id || "image"),
  addKind = ref(1),
  showGuides = ref(true),
  previewLoading = ref(false);
const stage = ref<HTMLElement>(),
  demoCanvas = ref<HTMLCanvasElement>(),
  inspector = ref<HTMLElement>(),
  layerMenu = ref<HTMLElement>(),
  png = ref(state.value.screen.pngBase64);
const contextMenu = ref<{ id: string; x: number; y: number } | null>(null);
const draggingLayer = ref<string | null>(null);
const dropTarget = ref<{ id: string; after: boolean } | null>(null);
let hydrating = false,
  revision = 0,
  acknowledged = 0,
  updating: Promise<boolean> | null = null,
  disposed = false;
let timer: ReturnType<typeof setTimeout> | undefined;
const activeWidget = computed(() =>
  project.value.Widgets.find((widget) => widget.Id === selected.value),
);
const hasImage = computed(() => Boolean(project.value.ImageFile));
const imageSelected = computed(() => selected.value === "image" && hasImage.value);
const layers = computed(() => {
  const stack = project.value.Widgets.map((widget) => ({
    id: widget.Id,
    widget: widget as ScreenWidget | null,
  }));
  if (hasImage.value)
    stack.splice(project.value.ImageLayerIndex ?? 0, 0, { id: "image", widget: null });
  return stack.reverse();
});
const frameSource = computed(() =>
  !png.value
    ? ""
    : png.value.startsWith("data:")
      ? png.value
      : `data:image/png;base64,${png.value}`,
);
const supportsUpload = computed(
  () =>
    isDesktop &&
    state.value.device.connected &&
    ((state.value.device.connection === "usb" &&
      /\bOL[4-9AB]\b/.test(state.value.device.firmware)) ||
      (state.value.device.connection === "radio" &&
        /\bOL[9AB]\b/.test(state.value.device.firmware) &&
        state.value.device.profile === 1)),
);
function hydrate(screen: ScreenState) {
  hydrating = true;
  project.value = clone(screen.project);
  png.value = screen.pngBase64;
  if (!imageSelected.value && !activeWidget.value)
    selected.value = layers.value[0]?.id ?? "";
  hydrating = false;
}
watch(
  () => state.value.screen,
  (screen) => {
    if (revision === acknowledged) hydrate(screen);
  },
  { deep: true },
);
watch(
  project,
  () => {
    drawDemo();
    if (hydrating) return;
    revision++;
    clearTimeout(timer);
    timer = setTimeout(updatePreview, 220);
  },
  { deep: true, flush: "sync" },
);
watch(busy, (isBusy) => {
  if (!isBusy && acknowledged < revision) void updatePreview();
});
async function updatePreview(): Promise<boolean> {
  clearTimeout(timer);
  if (!isDesktop) {
    acknowledged = revision;
    drawDemo();
    return true;
  }
  if (updating) return updating;
  if (acknowledged === revision || disposed) return true;
  if (busy.value) return false;
  previewLoading.value = true;
  updating = (async () => {
    try {
      while (acknowledged < revision && !disposed) {
        const current = revision;
        const result = await call<ScreenState>("screen.update", {
          project: clone(project.value),
        });
        acknowledged = current;
        if (current === revision && !disposed) {
          png.value = result.pngBase64;
          state.value.screen = result;
        }
      }
      return true;
    } catch (error) {
      notify(error instanceof Error ? error.message : String(error), true);
      return false;
    }
  })();
  try {
    return await updating;
  } finally {
    updating = null;
    previewLoading.value = false;
  }
}
async function perform(method: string) {
  if (await updatePreview()) {
    const result = await run(method);
    if (result && revision === acknowledged) hydrate(result.screen);
  }
}
function showImageSettings() {
  selected.value = "image";
  void nextTick(() => inspector.value?.scrollIntoView({ block: "nearest" }));
}
async function chooseImage() {
  await perform("screen.chooseImage");
  if (hasImage.value) showImageSettings();
}
async function live(enabled: boolean) {
  if (!enabled || (await updatePreview()))
    await run("screen.live", { enabled });
}
async function liveChanged(event: Event) {
  const input = event.target as HTMLInputElement;
  await live(input.checked);
  input.checked = state.value.screen.live;
}
function addWidget() {
  const widget = newWidget(addKind.value);
  widget.X = Math.min(3, project.value.Width - 1);
  widget.Y = Math.min(14, Math.max(0, project.value.Height - 12));
  widget.Width = Math.min(
    widget.Kind === batteryIndicatorKind ? batteryFullWidth : 60,
    project.value.Width - widget.X,
  );
  if (widget.Kind === batteryIndicatorKind) widget.Height = Math.min(9, project.value.Height - widget.Y);
  project.value.Widgets.push(widget);
  selected.value = widget.Id;
  if (project.value.Composition === 0) project.value.Composition = 2;
}
function remove(id = selected.value) {
  const index = layers.value.findIndex((layer) => layer.id === id);
  if (index < 0) return;
  const remaining = layers.value.filter((layer) => layer.id !== id);
  project.value = {
    ...project.value,
    ...layerOrder(remaining),
    ImageFile: id === "image" ? null : project.value.ImageFile,
    Composition: id === "image" ? 1 : project.value.Composition,
  };
  if (selected.value === id)
    selected.value = remaining[Math.min(index, remaining.length - 1)]?.id ?? "";
}
function duplicate() {
  if (!activeWidget.value) return;
  const widget = { ...clone(activeWidget.value), Id: newWidget().Id };
  widget.X = Math.min(project.value.Width - 1, widget.X + 3);
  widget.Y = Math.min(project.value.Height - 1, widget.Y + 2);
  project.value.Widgets.push(widget);
  selected.value = widget.Id;
}
function reorder(direction: number) {
  const index = layers.value.findIndex((layer) => layer.id === selected.value);
  const target = layers.value[index - direction];
  if (target) moveLayer(selected.value, target.id, direction < 0);
}
function layerOrder(ordered: typeof layers.value) {
  const stack = [...ordered].reverse();
  return {
    Widgets: stack.flatMap(({ widget }) => widget ? [widget] : []),
    ImageLayerIndex: Math.max(0, stack.findIndex(({ id }) => id === "image")),
  };
}
function moveLayer(id: string, target: string, after: boolean) {
  if (id === target) return;
  const layer = layers.value.find((entry) => entry.id === id);
  const ordered = layers.value.filter((entry) => entry.id !== id);
  const index = ordered.findIndex((entry) => entry.id === target);
  if (!layer || index < 0) return;
  ordered.splice(index + Number(after), 0, layer);
  if (ordered.every((entry, position) => entry.id === layers.value[position].id))
    return;
  project.value = { ...project.value, ...layerOrder(ordered) };
}
function dragLayer(event: DragEvent, id: string) {
  if (!event.dataTransfer || layers.value.length < 2) {
    event.preventDefault();
    return;
  }
  draggingLayer.value = id;
  selected.value = id;
  event.dataTransfer.effectAllowed = "move";
  event.dataTransfer.setData("text/plain", id);
}
function hoverLayer(event: DragEvent, id: string) {
  if (!draggingLayer.value || draggingLayer.value === id) {
    dropTarget.value = null;
    return;
  }
  event.preventDefault();
  if (event.dataTransfer) event.dataTransfer.dropEffect = "move";
  dropTarget.value = { id, after: dropsAfter(event) };
}
function dropsAfter(event: DragEvent) {
  const bounds = (event.currentTarget as HTMLElement).getBoundingClientRect();
  return event.clientY >= bounds.top + bounds.height / 2;
}
function endLayerDrag() {
  draggingLayer.value = null;
  dropTarget.value = null;
}
function dropLayer(event: DragEvent, id: string) {
  const dragged = draggingLayer.value;
  if (dragged && dragged !== id) {
    event.preventDefault();
    moveLayer(dragged, id, dropsAfter(event));
  }
  endLayerDrag();
}
let menuAnchor: HTMLElement | null = null;
function closeLayerMenu(event?: PointerEvent) {
  if (event && layerMenu.value?.contains(event.target as Node)) return;
  contextMenu.value = null;
  window.removeEventListener("pointerdown", closeLayerMenu);
}
async function openLayerMenu(event: MouseEvent, id: string) {
  menuAnchor = event.currentTarget as HTMLElement;
  const anchor = menuAnchor.getBoundingClientRect();
  const fromKeyboard = event.clientX === 0 && event.clientY === 0;
  contextMenu.value = {
    id,
    x: fromKeyboard ? anchor.left : event.clientX,
    y: fromKeyboard ? anchor.bottom : event.clientY,
  };
  window.addEventListener("pointerdown", closeLayerMenu);
  await nextTick();
  if (!layerMenu.value || !contextMenu.value) return;
  const { width, height } = layerMenu.value.getBoundingClientRect();
  contextMenu.value.x = Math.max(6, Math.min(contextMenu.value.x, window.innerWidth - width - 6));
  contextMenu.value.y = Math.max(6, Math.min(contextMenu.value.y, window.innerHeight - height - 6));
  layerMenu.value.querySelector("button")?.focus();
}
function cancelLayerMenu() {
  closeLayerMenu();
  menuAnchor?.focus();
}
function removeFromMenu() {
  if (contextMenu.value) remove(contextMenu.value.id);
  closeLayerMenu();
}
function geometry() {
  if (imageSelected.value)
    return {
      x: project.value.ImageX,
      y: project.value.ImageY,
      width: project.value.ImageWidth || project.value.Width,
      height: project.value.ImageHeight || project.value.Height,
    };
  const w = activeWidget.value;
  return w
    ? { x: w.X, y: w.Y, width: w.Width, height: w.Height }
    : { x: 0, y: 0, width: 1, height: 1 };
}
function setGeometry(key: "x" | "y" | "width" | "height", value: number) {
  const v = Math.round(value);
  if (imageSelected.value) {
    const names = {
      x: "ImageX",
      y: "ImageY",
      width: "ImageWidth",
      height: "ImageHeight",
    } as const;
    project.value[names[key]] = v;
  } else if (activeWidget.value) {
    const names = { x: "X", y: "Y", width: "Width", height: "Height" } as const;
    activeWidget.value[names[key]] = v;
  }
}
function rectStyle(layer: ScreenWidget | null) {
  const p = project.value;
  return {
    zIndex: selected.value === (layer?.Id ?? "image")
      ? layers.value.length
      : layers.value.length - 1 - layers.value.findIndex(({ id }) => id === (layer?.Id ?? "image")),
    left: `${(100 * (layer ? layer.X : p.ImageX)) / p.Width}%`,
    top: `${(100 * (layer ? layer.Y : p.ImageY)) / p.Height}%`,
    width: `${(100 * (layer ? layer.Width : p.ImageWidth || p.Width)) / p.Width}%`,
    height: `${(100 * (layer ? layer.Height : p.ImageHeight || p.Height)) / p.Height}%`,
  };
}
let drag: {
  id: number;
  x: number;
  y: number;
  startX: number;
  startY: number;
  width: number;
  height: number;
  resize: boolean;
  target: HTMLElement;
} | null = null;
function pointerDown(event: PointerEvent, id: string, resize = false) {
  if (event.button !== 0) return;
  event.preventDefault();
  event.stopPropagation();
  selected.value = id;
  const current = geometry(),
    target = event.currentTarget as HTMLElement;
  drag = {
    id: event.pointerId,
    x: event.clientX,
    y: event.clientY,
    startX: current.x,
    startY: current.y,
    width: current.width,
    height: current.height,
    resize,
    target,
  };
  target.setPointerCapture(event.pointerId);
}
function pointerMove(event: PointerEvent) {
  if (!drag || drag.id !== event.pointerId || !stage.value) return;
  const box = stage.value.getBoundingClientRect(),
    dx = Math.round(
      ((event.clientX - drag.x) * project.value.Width) / box.width,
    ),
    dy = Math.round(
      ((event.clientY - drag.y) * project.value.Height) / box.height,
    );
  if (drag.resize) {
    setGeometry(
      "width",
      Math.max(1, Math.min(project.value.Width - drag.startX, drag.width + dx)),
    );
    setGeometry(
      "height",
      Math.max(
        1,
        Math.min(project.value.Height - drag.startY, drag.height + dy),
      ),
    );
  } else {
    setGeometry(
      "x",
      Math.max(0, Math.min(project.value.Width - 1, drag.startX + dx)),
    );
    setGeometry(
      "y",
      Math.max(0, Math.min(project.value.Height - 1, drag.startY + dy)),
    );
  }
}
function pointerUp(event: PointerEvent) {
  if (drag?.id === event.pointerId) {
    if (drag.target.hasPointerCapture(event.pointerId))
      drag.target.releasePointerCapture(event.pointerId);
    drag = null;
  }
}
function keyMove(event: KeyboardEvent, id: string) {
  const directions: Record<string, [number, number]> = {
    ArrowLeft: [-1, 0],
    ArrowRight: [1, 0],
    ArrowUp: [0, -1],
    ArrowDown: [0, 1],
  };
  const direction = directions[event.key];
  if (!direction) return;
  event.preventDefault();
  selected.value = id;
  const current = geometry(),
    amount = event.shiftKey ? 5 : 1;
  setGeometry(
    "x",
    Math.max(
      0,
      Math.min(project.value.Width - 1, current.x + direction[0] * amount),
    ),
  );
  setGeometry(
    "y",
    Math.max(
      0,
      Math.min(project.value.Height - 1, current.y + direction[1] * amount),
    ),
  );
}
function label(widget: ScreenWidget) {
  return widget.Kind === 0
    ? widget.Text || t("Текст")
    : t(widgetLabels[widget.Kind]) || t("Виджет");
}
function drawDemoBattery(context: CanvasRenderingContext2D, widget: ScreenWidget) {
  const { X: x, Y: y, Height: height } = widget;
  const width = Math.min(widget.Width, batteryFullWidth);
  if (width < 5 || height < 3) return;
  const bodyWidth = width - 2;
  context.fillRect(x, y, bodyWidth, 1);
  context.fillRect(x, y + height - 1, bodyWidth, 1);
  context.fillRect(x, y + 1, 1, height - 2);
  context.fillRect(x + bodyWidth - 1, y + 1, 1, height - 2);
  const terminalHeight = Math.max(1, Math.floor(height / 3));
  context.fillRect(x + bodyWidth, y + Math.floor((height - terminalHeight) / 2), 2, terminalHeight);
  const innerWidth = bodyWidth - 2;
  const dashWidth = Math.min(3, innerWidth);
  context.fillRect(x + 1 + Math.floor((innerWidth - dashWidth) / 2), y + Math.floor(height / 2), dashWidth, 1);
}
function drawDemo() {
  nextTick(() => {
    if (isDesktop || !demoCanvas.value) return;
    const context = demoCanvas.value.getContext("2d");
    if (!context) return;
    context.fillStyle = "#050607";
    context.fillRect(0, 0, project.value.Width, project.value.Height);
    if (project.value.Composition === 0) return;
    for (const w of project.value.Widgets) {
      context.save();
      context.beginPath();
      context.rect(w.X, w.Y, w.Width, w.Height);
      context.clip();
      if (w.ClearBackground) {
        context.fillStyle = w.Lit ? "#050607" : "#edf2ee";
        context.fillRect(w.X, w.Y, w.Width, w.Height);
      }
      context.fillStyle = w.Lit ? "#edf2ee" : "#050607";
      if (w.Kind === batteryIndicatorKind) {
        drawDemoBattery(context, w);
        context.restore();
        continue;
      }
      context.font = `${w.Bold ? "bold " : ""}${w.FontSize}px monospace`;
      context.textBaseline = "top";
      const text =
        w.Kind === 0
          ? w.Text
          : w.Kind === 1
            ? "12:34"
            : `${w.ShowLabel ? t(widgetLabels[w.Kind]) + " " : ""}—`;
      context.fillText(text, w.X, w.Y);
      context.restore();
    }
  });
}
watch(demoCanvas, drawDemo);
watch(locale, drawDemo);
onBeforeUnmount(() => {
  disposed = true;
  clearTimeout(timer);
  closeLayerMenu();
});
onDeactivated(closeLayerMenu);
</script>
<template>
  <div class="oled-toolbar">
    <div class="toolbar-group">
      <button
        class="button"
        :disabled="busy || !isDesktop"
        @click="perform('screen.open')"
      >
        {{ t("Открыть проект") }}</button><button
        class="button"
        :disabled="busy || !isDesktop"
        @click="perform('screen.save')"
      >
        <AppIcon name="save" :size="15" />{{ t("Сохранить") }}</button
      ><button
        class="button subtle"
        :disabled="busy || !isDesktop"
        @click="perform('screen.exportPng')"
      >
        {{ t("Экспорт PNG") }}
      </button>
    </div>
    <div class="toolbar-group">
      <label class="checkbox"
        ><input
          type="checkbox"
          :checked="state.screen.live"
          :disabled="busy || !supportsUpload"
          @change="liveChanged($event)"
        /><span class="live-dot" :class="{ enabled: state.screen.live }" />{{
          t("Обновлять в реальном времени")
        }}</label
      ><button
        class="button primary"
        :disabled="busy || !supportsUpload"
        @click="perform('screen.upload')"
      >
        <AppIcon name="upload" :size="16" />{{ t("На OLED") }}
      </button>
    </div>
  </div>
  <div
    v-if="isDesktop && state.device.connected && !supportsUpload"
    class="notice"
  >
    <AppIcon name="info" />
    <p>
      {{
        t(
          "Для OLED по USB нужна OL4 или новее. Для 2.4G нужна OL9, OLA или OLB и активный профиль 1. Текущая прошивка:",
        )
      }}
      {{ firmwareName(state.device.firmware) || t("не определена")
      }}{{ t(". Редактор доступен.") }}
    </p>
  </div>
  <div class="oled-layout">
    <div class="oled-workspace">
      <section class="panel preview-panel">
        <div class="section-heading">
          <div>
            <span class="eyebrow">{{ t("ХОЛСТ OLED") }}</span>
            <h2>{{ t("120 × 28 пикселей") }}</h2>
          </div>
          <label class="checkbox small"
            ><input v-model="showGuides" type="checkbox" />{{
              t("Границы слоёв")
            }}</label
          >
        </div>
        <div class="oled-preview-surround">
          <span class="ruler-top">0 <span>60</span> 120 px</span>
          <div
            ref="stage"
            class="oled-stage"
            :class="{ guides: showGuides }"
            :style="{ aspectRatio: `${project.Width}/${project.Height}` }"
            @pointermove="pointerMove"
            @pointerup="pointerUp"
            @pointercancel="pointerUp"
          >
            <img
              v-if="frameSource && isDesktop"
              :src="frameSource"
              :alt="t('Предпросмотр экрана, отрисованный приложением')"
              draggable="false"
            /><canvas
              v-else-if="!isDesktop"
              ref="demoCanvas"
              :width="project.Width"
              :height="project.Height"
              :aria-label="t('Демонстрационный макет без системных данных')"
            />
            <div v-else class="preview-placeholder">
              {{
                previewLoading
                  ? t("Отрисовка…")
                  : t("Предпросмотр появится после отрисовки")
              }}
            </div>
            <template v-if="showGuides"
              ><div
                v-if="hasImage && project.Composition !== 1"
                class="layer-rect image-rect"
                :class="{ selected: imageSelected }"
                :style="rectStyle(null)"
                tabindex="0"
                :aria-label="t('Положение изображения')"
                @pointerdown="pointerDown($event, 'image')"
                @contextmenu.prevent="openLayerMenu($event, 'image')"
                @keydown="keyMove($event, 'image')"
              >
                <span
                  v-if="imageSelected"
                  class="resize-handle"
                  @pointerdown="pointerDown($event, 'image', true)"
                />
              </div>
              <div
                v-for="widget in project.Composition === 0
                  ? []
                  : project.Widgets"
                :key="widget.Id"
                class="layer-rect"
                :class="{ selected: selected === widget.Id }"
                :style="rectStyle(widget)"
                tabindex="0"
                :aria-label="label(widget)"
                @pointerdown="pointerDown($event, widget.Id)"
                @contextmenu.prevent="openLayerMenu($event, widget.Id)"
                @keydown="keyMove($event, widget.Id)"
              >
                <span
                  v-if="selected === widget.Id"
                  class="resize-handle"
                  @pointerdown="pointerDown($event, widget.Id, true)"
                /></div
            ></template>
          </div>
          <div class="preview-meta">
            <span>{{
              isDesktop
                ? t("Нативная отрисовка")
                : t("Демонстрация · значения датчиков не подключены")
            }}</span><span>{{
              previewLoading
                ? t("Обновляется…")
                : state.screen.live
                  ? "● LIVE"
                  : t("Предпросмотр")
            }}</span>
          </div>
        </div>
        <p class="hint">
          {{
            t(
              "Перетаскивайте слой мышью. Уголок меняет размер. Стрелки — сдвиг на 1 px, Shift + стрелки — на 5 px.",
            )
          }}
        </p>
        <p v-if="state.screen.status" class="screen-status" role="status">
          {{ oledStatus(state.screen.status) }}
        </p>
      </section>
      <section class="panel">
        <div class="section-heading">
          <h2>{{ t("Слои") }}</h2>
          <button
            v-if="hasImage"
            class="button subtle image-settings-button"
            @click="showImageSettings"
          >
            <AppIcon name="settings" :size="16" />{{ t("Настройки изображения") }}
          </button>
        </div>
        <div class="add-layer">
          <select v-model="addKind" :aria-label="t('Добавляемый виджет')">
            <option
              v-for="(name, index) in widgetLabels"
              :key="name"
              :value="index"
            >
              {{ t(name) }}
            </option></select
          ><button
            class="button"
            :disabled="project.Widgets.length >= 128"
            @click="addWidget"
          >
            <AppIcon name="plus" :size="16" />{{ t("Виджет") }}</button
          ><button
            class="button"
            :disabled="busy || !isDesktop"
            @click="chooseImage"
          >
            {{ t("+ Картинка") }}
          </button>
        </div>
        <div class="layer-list">
          <button
            v-for="layer in layers"
            :key="layer.id"
            class="layer-row"
            :class="{
              selected: selected === layer.id,
              dragging: draggingLayer === layer.id,
              'drop-before': dropTarget?.id === layer.id && !dropTarget.after,
              'drop-after': dropTarget?.id === layer.id && dropTarget.after,
            }"
            :aria-pressed="selected === layer.id"
            :draggable="layers.length > 1"
            @click="selected = layer.id"
            @keydown.delete.prevent="remove(layer.id)"
            @contextmenu.prevent="openLayerMenu($event, layer.id)"
            @dragstart="dragLayer($event, layer.id)"
            @dragover="hoverLayer($event, layer.id)"
            @dragleave="dropTarget = null"
            @drop="dropLayer($event, layer.id)"
            @dragend="endLayerDrag"
          >
            <span class="layer-kind" aria-hidden="true">
              <AppIcon v-if="!layer.widget" name="screen" :size="14" />
              <template v-else>{{ layer.widget.Kind === 0 ? "T" : "◈" }}</template>
            </span>
            <span class="layer-description">
              <strong>{{ layer.widget ? label(layer.widget) : t("Изображение") }}</strong>
              <small v-if="layer.widget">
                {{ layer.widget.Width }} × {{ layer.widget.Height }} · X {{ layer.widget.X }}, Y {{ layer.widget.Y }}
              </small>
              <small v-else>{{ project.ImageFile?.split(/[\\/]/).pop() }}</small>
            </span>
            <AppIcon v-if="layers.length > 1" class="layer-grip" name="grip" :size="16" />
          </button>
          <p v-if="!hasImage && !project.Widgets.length" class="empty-inline">
            {{ t("Добавьте картинку или первый виджет.") }}
          </p>
        </div>
        <p v-if="layers.length" class="hint layer-list-hint">
          {{ t("Перетащите слой, чтобы изменить порядок. ПКМ открывает меню слоя.") }}
        </p>
        <label class="field-label composition-select"
          >{{ t("Содержимое экрана")
          }}<select v-model="project.Composition">
            <option :value="1">{{ t("Только виджеты") }}</option>
            <option :value="0">{{ t("Только изображение") }}</option>
            <option :value="2">{{ t("Изображение и виджеты") }}</option>
          </select></label
        >
      </section>
    </div>
    <section ref="inspector" class="panel layer-properties">
      <div class="section-heading">
        <div>
          <span class="eyebrow">{{ t("СВОЙСТВА СЛОЯ") }}</span>
          <h2>
            {{
              imageSelected
                ? t("Изображение")
                : activeWidget
                  ? label(activeWidget)
                  : t("Выберите слой")
            }}
          </h2>
        </div>
        <button
          v-if="activeWidget || (hasImage && imageSelected)"
          class="icon-button danger"
          :aria-label="t('Удалить выбранный слой')"
          :title="t('Удалить слой')"
          @click="remove()"
        >
          <AppIcon name="trash" :size="17" />
        </button>
      </div>
      <template v-if="activeWidget || imageSelected">
        <div v-if="imageSelected" class="image-controls">
          <label class="field-label">
            {{ t("Масштабирование") }}
            <select v-model="project.ImageOptions.Resize">
              <option :value="0">{{ t("Вписать целиком") }}</option>
              <option :value="1">{{ t("Заполнить с обрезкой") }}</option>
              <option :value="2">{{ t("Растянуть") }}</option>
            </select>
          </label>
          <RangeField
            v-model="project.ImageOptions.Threshold"
            :label="t('Порог чёрного и белого')"
            :min="0"
            :max="255"
          />
          <div class="property-toggles">
            <label class="checkbox">
              <input v-model="project.ImageOptions.Dither" type="checkbox" />
              {{ t("Дизеринг · передать полутона точками") }}
            </label>
            <label class="checkbox">
              <input v-model="project.ImageOptions.Invert" type="checkbox" />
              {{ t("Инвертировать цвета") }}
            </label>
            <label class="checkbox">
              <input v-model="project.ImageOptions.BackgroundLit" type="checkbox" />
              {{ t("Светлый фон прозрачных областей") }}
            </label>
          </div>
          <button class="button" :disabled="busy || !isDesktop" @click="chooseImage">
            {{ t("Заменить изображение") }}
          </button>
        </div>
        <h3 class="layer-field-group">{{ t("Положение и размер") }}</h3>
        <div class="geometry-fields">
          <RangeField
            :label="t('По горизонтали · X')"
            :model-value="geometry().x"
            :min="0"
            :max="project.Width - 1"
            unit="px"
            @update:model-value="setGeometry('x', $event)"
          /><RangeField
            :label="t('По вертикали · Y')"
            :model-value="geometry().y"
            :min="0"
            :max="project.Height - 1"
            unit="px"
            @update:model-value="setGeometry('y', $event)"
          /><RangeField
            :label="t('Ширина')"
            :model-value="geometry().width"
            :min="1"
            :max="project.Width"
            unit="px"
            @update:model-value="setGeometry('width', $event)"
          /><RangeField
            :label="t('Высота')"
            :model-value="geometry().height"
            :min="1"
            :max="project.Height"
            unit="px"
            @update:model-value="setGeometry('height', $event)"
          />
        </div>
        <hr v-if="activeWidget" />
        <template v-if="activeWidget"
          ><label class="field-label"
            >{{ t("Тип виджета")
            }}<select v-model="activeWidget.Kind">
              <option
                v-for="(name, index) in widgetLabels"
                :key="name"
                :value="index"
              >
                {{ t(name) }}
              </option>
            </select></label
          ><label v-if="activeWidget.Kind === 0" class="field-label"
            >{{ t("Текст")
            }}<textarea
              v-model="activeWidget.Text"
              maxlength="4096"
              rows="2"
            /></label
          ><label v-if="activeWidget.Kind === 1" class="field-label"
            >{{ t("Формат часов")
            }}<select v-model="activeWidget.Format">
              <option value="HH:mm">{{ t("Часы и минуты · 23:45") }}</option>
              <option value="HH:mm:ss">
                {{ t("С секундами · 23:45:06") }}
              </option>
              <option value="dd.MM">{{ t("Дата · 16.11") }}</option>
              <option value="dd.MM HH:mm">{{ t("Дата и время") }}</option>
            </select></label
          ><RangeField
            v-if="activeWidget.Kind !== batteryIndicatorKind"
            v-model="activeWidget.FontSize"
            :label="t('Размер шрифта')"
            :min="4"
            :max="28"
            unit="px"
          />
          <p v-if="activeWidget.Kind === batteryIndicatorKind" class="hint">
            {{ t("Деление на каждые 10% заряда. При ширине 113 px каждое деление занимает 10 px.") }}
          </p>
          <div class="property-toggles">
            <label v-if="activeWidget.Kind !== batteryIndicatorKind" class="checkbox"
              ><input v-model="activeWidget.Bold" type="checkbox" />{{
                t("Жирный шрифт")
              }}</label
            ><label class="checkbox"
              ><input v-model="activeWidget.Lit" type="checkbox" />{{
                t(activeWidget.Kind === batteryIndicatorKind ? "Светлый индикатор" : "Светлый текст")
              }}</label
            ><label class="checkbox"
              ><input
                v-model="activeWidget.ClearBackground"
                type="checkbox"
              />{{ t(activeWidget.Kind === batteryIndicatorKind ? "Очищать фон под индикатором" : "Очищать фон под текстом") }}</label
            ><label v-if="activeWidget.Kind > 1 && activeWidget.Kind !== batteryIndicatorKind" class="checkbox"
              ><input v-model="activeWidget.ShowLabel" type="checkbox" />{{
                t("Показывать название показателя")
              }}</label
            >
          </div>
        </template>
        <div v-if="layers.length > 1 || activeWidget" class="toolbar-group layer-actions">
          <button class="button subtle" :disabled="layers[0]?.id === selected" @click="reorder(1)">
            {{ t("Выше") }}
          </button>
          <button class="button subtle" :disabled="layers[layers.length - 1]?.id === selected" @click="reorder(-1)">
            {{ t("Ниже") }}
          </button>
          <button v-if="activeWidget" class="button subtle" :disabled="project.Widgets.length >= 128" @click="duplicate">
            {{ t("Копия") }}
          </button>
        </div>
      </template>
      <div v-else class="empty-inline">
        {{ t("Выберите слой в списке или добавьте новый.") }}
      </div>
    </section>
  </div>
  <Teleport to="body">
    <div
      v-if="contextMenu"
      ref="layerMenu"
      class="layer-menu"
      role="menu"
      :aria-label="t('Меню слоя')"
      :style="{ left: contextMenu.x + 'px', top: contextMenu.y + 'px' }"
      @keydown.esc.prevent.stop="cancelLayerMenu"
    >
      <button type="button" class="button danger" role="menuitem" @click="removeFromMenu">
        <AppIcon name="trash" :size="16" />{{ t("Удалить слой") }}
      </button>
    </div>
  </Teleport>
</template>
