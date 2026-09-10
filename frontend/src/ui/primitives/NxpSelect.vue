<script setup lang="ts">
export interface NxpOption { value: string; label: string; disabled?: boolean }
const props = withDefaults(defineProps<{ modelValue?: string | string[]; options?: NxpOption[]; multiple?: boolean; disabled?: boolean; ariaLabel?: string }>(), { modelValue: "", options: () => [], multiple: false, disabled: false, ariaLabel: "" });
const emit = defineEmits<{ "update:modelValue": [value: string | string[]]; change: [value: string | string[]] }>();
function update(event: Event) { const select = event.target as HTMLSelectElement; const value = props.multiple ? Array.from(select.selectedOptions).map(option => option.value) : select.value; emit("update:modelValue", value); emit("change", value); }
</script>

<template><select class="nxp-input nxp-select" :value="props.modelValue" :multiple="props.multiple" :disabled="props.disabled" :aria-label="props.ariaLabel || undefined" @change="update"><option v-for="option in props.options" :key="option.value" :value="option.value" :disabled="option.disabled">{{ option.label }}</option><slot /></select></template>

<style>
.nxp-select { appearance: none; background: var(--input-bg, transparent); }
.nxp-select[multiple] { min-height: 96px; padding-block: var(--nx-space-2); }
</style>
