<script setup lang="ts">
import { ref } from "vue";

const props = withDefaults(defineProps<{
  id?: string;
  modelValue?: string;
  placeholder?: string;
  disabled?: boolean;
  type?: string;
  maxlength?: number | string;
  readonly?: boolean;
  autocomplete?: string;
  ariaLabel?: string;
}>(), {
  id: undefined,
  modelValue: "",
  placeholder: "",
  disabled: false,
  type: "text",
  maxlength: undefined,
  readonly: false,
  autocomplete: undefined,
  ariaLabel: undefined,
});
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
const input = ref<HTMLInputElement | null>(null);
function update(event: Event) { const value = (event.target as HTMLInputElement).value; emit("update:modelValue", value); }
function change(event: Event) { emit("change", (event.target as HTMLInputElement).value); }
defineExpose({ focus: () => input.value?.focus() });
</script>

<template><input ref="input" class="nxp-input" :id="props.id" :type="props.type" :value="props.modelValue" :placeholder="props.placeholder" :disabled="props.disabled" :maxlength="props.maxlength" :readonly="props.readonly" :autocomplete="props.autocomplete" :aria-label="props.ariaLabel" @input.stop="update" @change.stop="change" /></template>

<style>
.nxp-input { width: 100%; min-height: var(--nx-control-height); border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); padding: 0 var(--nx-space-3); background: var(--input-bg, transparent); color: var(--nx-color-text); font: inherit; }
.nxp-input:focus { border-color: var(--nx-color-accent); box-shadow: var(--nx-focus-ring); outline: 0; }
.nxp-input:disabled { opacity: .55; }
</style>
