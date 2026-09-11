<script setup lang="ts">
defineProps<{ label?: string; description?: string; help?: string; error?: string; required?: boolean }>();
</script>

<template><label class="nxp-field" :data-help="help || undefined"><span v-if="label" class="nxp-field-label">{{ label }}<span v-if="required" aria-hidden="true"> *</span></span><slot /><span v-if="description" class="nxp-field-description">{{ description }}</span><span v-if="error" class="nxp-field-error" role="alert">{{ error }}</span></label></template>

<style>
.nxp-field { display: grid; min-width: 0; gap: var(--nx-space-2); color: var(--nx-color-text); }
.nxp-field-label { font-size: 12px; font-weight: 600; }
.nxp-field-description { color: var(--nx-color-muted); font-size: 11px; line-height: 1.45; }
.nxp-field-error { color: var(--nx-color-danger); font-size: 11px; }
</style>
