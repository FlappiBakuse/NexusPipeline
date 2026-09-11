<script setup lang="ts">
import { t } from "@legacy/core/i18n.js";

const props = withDefaults(
  defineProps<{
    id?: string;
    modelValue?: number | string;
    min?: number;
    max?: number;
    step?: number;
    placeholder?: string;
    help?: string;
    disabled?: boolean;
    ariaLabel?: string;
  }>(),
  { id: "", modelValue: "", disabled: false, step: 1, placeholder: "", help: "", ariaLabel: "" },
);
const emit = defineEmits<{
  "update:modelValue": [value: number | string];
  change: [value: number | string];
}>();

function parse(value: string): number | string {
  return value.trim() === "" ? "" : Number(value);
}
function update(event: Event) {
  emit("update:modelValue", parse((event.target as HTMLInputElement).value));
}
function change(event: Event) {
  emit("change", parse((event.target as HTMLInputElement).value));
}
function stepValue(direction: 1 | -1) {
  const current = Number(props.modelValue);
  const base = Number.isFinite(current) ? current : Number(props.min) || 0;
  const step = Number(props.step) || 1;
  let next = base + direction * step;
  if (props.min !== undefined) next = Math.max(props.min, next);
  if (props.max !== undefined) next = Math.min(props.max, next);
  next = Number.isInteger(step) ? Math.round(next) : Number(next.toFixed(8));
  emit("update:modelValue", next);
  emit("change", next);
}
</script>

<template>
  <div class="nxp-number" data-nxp-number :data-help="props.help || undefined">
    <input
      :id="props.id || undefined"
      class="nxp-number-input"
      data-nxp-number-value
      type="text"
      inputmode="decimal"
      :value="modelValue"
      :min="min"
      :max="max"
      :step="step"
      :placeholder="placeholder || undefined"
      :disabled="disabled"
      :aria-label="ariaLabel || undefined"
      @input="update"
      @change="change"
    />
    <span class="nxp-number-actions"
      ><button
        type="button"
        class="nxp-number-step"
        data-nxp-step="increment"
        :disabled="disabled"
        :aria-label="t('common.increase')"
        @click="stepValue(1)"
      >
        ＋</button
      ><button
        type="button"
        class="nxp-number-step"
        data-nxp-step="decrement"
        :disabled="disabled"
        :aria-label="t('common.decrease')"
        @click="stepValue(-1)"
      >
        －
      </button></span
    >
  </div>
</template>

<style>
.nxp-number {
  display: grid;
  min-width: 0;
  grid-template-columns: minmax(0, 1fr) var(--field-action-width, 40px);
}
.nxp-number-input {
  width: 100%;
  min-width: 0;
  height: 40px;
  padding: 0 12px;
  border: 1px solid var(--content-control-border, var(--nx-color-border));
  border-radius: 8px 0 0 8px;
  outline: none;
  background: var(--content-control, transparent);
  color: var(--nx-color-text);
  font: inherit;
}
.nxp-number-input:focus {
  border-color: var(--accent, var(--nx-color-accent));
}
.nxp-number-input:disabled {
  cursor: not-allowed;
  opacity: 0.45;
}
.nxp-number-actions {
  display: grid;
  grid-template-rows: 1fr 1fr;
}
.nxp-number-step {
  width: var(--field-action-width, 40px);
  min-width: var(--field-action-width, 40px);
  min-height: 20px;
  padding: 0;
  border-radius: 0;
  font: inherit;
  line-height: 1;
}
.nxp-number-step:first-child {
  border-radius: 0 8px 0 0;
}
.nxp-number-step:last-child {
  border-top: 0;
  border-radius: 0 0 8px 0;
}
</style>
