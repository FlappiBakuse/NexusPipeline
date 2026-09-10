<script setup lang="ts">
const props = withDefaults(defineProps<{ modelValue?: number | string; min?: number; max?: number; step?: number; disabled?: boolean; ariaLabel?: string }>(), { modelValue: "", disabled: false, step: 1, ariaLabel: "" });
const emit = defineEmits<{ "update:modelValue": [value: number | string]; change: [value: number | string] }>();
function update(event: Event) { const raw = (event.target as HTMLInputElement).value; const value = raw === "" ? "" : Number(raw); emit("update:modelValue", value); }
function change(event: Event) { const raw = (event.target as HTMLInputElement).value; emit("change", raw === "" ? "" : Number(raw)); }
</script>

<template><input class="nxp-input" type="number" :value="props.modelValue" :min="props.min" :max="props.max" :step="props.step" :disabled="props.disabled" :aria-label="props.ariaLabel || undefined" @input="update" @change="change" /></template>
