<script setup lang="ts">
import { t } from "@legacy/core/i18n.js";
const props = withDefaults(defineProps<{ id?: string; modelValue?: boolean; disabled?: boolean; label?: string; ariaLabel?: string; semanticRole?: "switch" | "button" }>(), { id: "", modelValue: false, disabled: false, label: "", ariaLabel: "", semanticRole: "button" });
const emit = defineEmits<{ "update:modelValue": [value: boolean]; change: [value: boolean] }>();
function toggle() { if (props.disabled) return; const value = !props.modelValue; emit("update:modelValue", value); emit("change", value); }
</script>

<template><button :id="props.id || undefined" class="mode-toggle switch-control nxp-switch" type="button" :role="props.semanticRole === 'switch' ? 'switch' : undefined" :aria-checked="props.semanticRole === 'switch' ? props.modelValue : undefined" :aria-pressed="props.modelValue" :aria-disabled="props.disabled" :aria-label="props.ariaLabel || props.label || undefined" :disabled="props.disabled" :data-state="props.modelValue ? 'on' : 'off'" @click="toggle"><span class="switch-track" aria-hidden="true"><span class="switch-thumb" /></span><span v-if="label" class="nxp-switch-label">{{ label }}</span><span class="sr-only" data-switch-state>{{ props.modelValue ? t('common.enabled') : t('common.disabled') }}</span></button></template>
