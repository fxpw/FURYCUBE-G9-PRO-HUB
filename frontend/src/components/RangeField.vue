<script setup lang="ts">
defineProps<{
  modelValue: number;
  label: string;
  min: number;
  max: number;
  step?: number;
  unit?: string;
  disabled?: boolean;
}>();
const emit = defineEmits<{ "update:modelValue": [value: number] }>();
function change(event: Event) {
  const input = event.target as HTMLInputElement;
  const value = Number(input.value);
  if (Number.isFinite(value))
    emit(
      "update:modelValue",
      Math.max(Number(input.min), Math.min(Number(input.max), value)),
    );
}
</script>
<template>
  <label class="range-field"
    ><span class="field-heading"
      ><span>{{ label }}</span
      ><span class="number-unit"
        ><input
          type="number"
          :aria-label="label"
          :value="modelValue"
          :min="min"
          :max="max"
          :step="step || 1"
          :disabled="disabled"
          @change="change"
        /><small>{{ unit }}</small></span
      ></span
    ><input
      type="range"
      :aria-label="label"
      :value="modelValue"
      :min="min"
      :max="max"
      :step="step || 1"
      :disabled="disabled"
      @input="change"
  /></label>
</template>
