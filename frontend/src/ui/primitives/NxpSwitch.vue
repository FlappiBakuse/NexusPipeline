<script setup lang="ts">
import { t } from "../../platform/i18n";
const props = withDefaults(defineProps<{ id?: string; modelValue?: boolean; disabled?: boolean; label?: string; ariaLabel?: string; semanticRole?: "switch" | "button" }>(), { id: "", modelValue: false, disabled: false, label: "", ariaLabel: "", semanticRole: "button" });
const emit = defineEmits<{ "update:modelValue": [value: boolean]; change: [value: boolean] }>();
function toggle() { if (props.disabled) return; const value = !props.modelValue; emit("update:modelValue", value); emit("change", value); }
</script>

<template><button :id="props.id || undefined" class="nxp-switch-control nxp-switch" type="button" :role="props.semanticRole === 'switch' ? 'switch' : undefined" :aria-checked="props.semanticRole === 'switch' ? props.modelValue : undefined" :aria-pressed="props.modelValue" :aria-disabled="props.disabled" :aria-label="props.ariaLabel || props.label || undefined" :disabled="props.disabled" :data-state="props.modelValue ? 'on' : 'off'" @click="toggle"><span class="nxp-switch-track" aria-hidden="true"><span class="nxp-switch-thumb" /></span><span v-if="label" class="nxp-switch-label">{{ label }}</span><span class="sr-only" data-switch-state>{{ props.modelValue ? t('common.enabled') : t('common.disabled') }}</span></button></template>

<style>
:host {
  display: inline-flex;
  min-width: 0;
  vertical-align: middle;
}
.nxp-switch-control {
  display: inline-flex;
  flex: 0 0 auto;
  width: var(--nx-switch-control-width, 52px);
  min-width: var(--nx-switch-control-width, 52px);
  height: var(--nx-switch-control-height, 30px);
  min-height: var(--nx-switch-control-height, 30px);
  align-items: center;
  justify-content: center;
  padding: 0 2px;
  border: 1px solid var(--content-control-border, var(--nx-color-border));
  border-radius: 999px;
  background: var(--content-control, var(--nx-color-surface));
  color: transparent;
  box-shadow: none;
}
.nxp-switch-control:hover { background: var(--content-control-hover, var(--nx-color-surface)); }
.nxp-switch-control:focus-visible { outline: 2px solid var(--accent, var(--nx-color-accent)); outline-offset: 2px; }
.nxp-switch-control:disabled,
.nxp-switch-control[aria-disabled="true"] { cursor: not-allowed; opacity: .45; }
.nxp-switch-control:disabled:hover,
.nxp-switch-control[aria-disabled="true"]:hover { background: var(--content-control, var(--nx-color-surface)); }
.nxp-switch-control:disabled:focus-visible,
.nxp-switch-control[aria-disabled="true"]:focus-visible { outline: none; }
.nxp-switch-track {
  position: relative;
  display: block;
  width: var(--nx-switch-track-width, 100%);
  height: var(--nx-switch-track-height, 100%);
  border-radius: inherit;
  background: var(--border-strong, var(--nx-color-border));
  transition: background .18s ease;
}
.nxp-switch-thumb {
  position: absolute;
  top: 4px;
  left: 4px;
  width: var(--nx-switch-thumb-size, 20px);
  height: var(--nx-switch-thumb-size, 20px);
  border-radius: 50%;
  background: var(--content-card, var(--nx-color-surface));
  box-shadow: 0 1px 3px rgba(0, 0, 0, .18);
  transition: transform .18s ease;
}
.nxp-switch-control[data-state="on"] .nxp-switch-track { background: var(--accent, var(--nx-color-accent)); }
.nxp-switch-control[data-state="on"] .nxp-switch-thumb { transform: translateX(var(--nx-switch-thumb-translate, 22px)); }
.nxp-switch-label { color: var(--nx-color-text); }
</style>
