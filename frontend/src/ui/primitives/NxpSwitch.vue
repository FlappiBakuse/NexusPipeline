<script setup lang="ts">
const props = withDefaults(defineProps<{ modelValue?: boolean; disabled?: boolean; label?: string }>(), { modelValue: false, disabled: false, label: "" });
const emit = defineEmits<{ "update:modelValue": [value: boolean]; change: [value: boolean] }>();
function toggle() { if (props.disabled) return; const value = !props.modelValue; emit("update:modelValue", value); emit("change", value); }
</script>

<template><button class="nxp-switch" type="button" role="switch" :aria-checked="props.modelValue" :aria-label="props.label || undefined" :disabled="props.disabled" @click="toggle"><span class="nxp-switch-track"><span class="nxp-switch-thumb" /></span><span v-if="label" class="nxp-switch-label">{{ label }}</span></button></template>

<style>
.nxp-switch { display: inline-flex; min-height: var(--nx-control-height); align-items: center; gap: var(--nx-space-2); border: 0; padding: 0; background: transparent; color: var(--nx-color-text); font: inherit; cursor: pointer; }
.nxp-switch:disabled { opacity: .55; cursor: not-allowed; }
.nxp-switch-track { display: inline-flex; width: 42px; height: 24px; align-items: center; padding: 3px; border-radius: 999px; background: var(--border-strong, rgba(160, 180, 210, .35)); transition: background .16s ease; }
.nxp-switch-thumb { width: 18px; height: 18px; border-radius: 50%; background: var(--nx-color-text); transition: transform .16s ease; }
.nxp-switch[aria-checked="true"] .nxp-switch-track { background: var(--nx-color-accent); }
.nxp-switch[aria-checked="true"] .nxp-switch-thumb { transform: translateX(18px); background: var(--on-accent, #08111f); }
</style>
