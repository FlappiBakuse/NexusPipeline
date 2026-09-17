<script setup lang="ts">
import { useSlotPresence } from "../slots";
const hasSlot = useSlotPresence();
const props = withDefaults(defineProps<{
  label?: string;
  help?: string;
  description?: string;
  error?: string;
  required?: boolean;
  for?: string;
}>(), {
  label: "",
  help: "",
  description: "",
  error: "",
  required: false,
  for: "",
});
</script>

<template>
  <label class="nxp-field" :for="props.for || undefined" :data-help="props.help || undefined">
    <span v-if="props.label || hasSlot('label')" class="nxp-field-label">
      <slot name="label">{{ props.label }}</slot><span v-if="props.required" aria-hidden="true"> *</span>
    </span>
    <span v-if="hasSlot('help')" class="nxp-field-help"><slot name="help" /></span>
    <slot />
    <span v-if="props.description || hasSlot('description')" class="nxp-field-description"><slot name="description">{{ props.description }}</slot></span>
    <span v-if="props.error || hasSlot('error')" class="nxp-field-error" role="alert" aria-live="polite"><slot name="error">{{ props.error }}</slot></span>
  </label>
</template>

<style>
.nxp-field { display: grid; min-width: 0; margin: 0; gap: var(--nx-space-2); color: var(--nx-color-text); }
.nxp-field-label { font-size: 12px; font-weight: 600; }
.nxp-field-label > [aria-hidden="true"] { color: var(--nx-color-danger); }
.nxp-field-help { color: var(--nx-color-muted); font-size: 11px; line-height: 1.45; }
.nxp-field-description { color: var(--nx-color-muted); font-size: 11px; line-height: 1.45; }
.nxp-field-error { color: var(--nx-color-danger); font-size: 11px; line-height: 1.45; }
</style>
