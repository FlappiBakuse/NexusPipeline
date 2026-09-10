<script setup lang="ts">
const props = withDefaults(defineProps<{ modelValue?: number | string; min?: number; max?: number; step?: number; disabled?: boolean; ariaLabel?: string }>(), { modelValue: 0, min: 0, max: 100, step: 1, disabled: false, ariaLabel: "" });
const emit = defineEmits<{ "update:modelValue": [value: number]; change: [value: number] }>();
function update(event: Event) { emit("update:modelValue", Number((event.target as HTMLInputElement).value)); }
function change(event: Event) { emit("change", Number((event.target as HTMLInputElement).value)); }
</script>

<template><input class="nxp-range" type="range" :value="props.modelValue" :min="props.min" :max="props.max" :step="props.step" :disabled="props.disabled" :aria-label="props.ariaLabel || undefined" @input="update" @change="change" /></template>

<style>
.nxp-range { width: 100%; min-height: var(--nx-control-height); accent-color: var(--nx-color-accent); }
</style>
