<script setup lang="ts">
withDefaults(defineProps<{ label: string; pressed?: boolean; expanded?: boolean; disabled?: boolean; type?: "button" | "submit" | "reset" }>(), { pressed: false, expanded: undefined, disabled: false, type: "button" });
</script>

<template><button class="icon-button nxp-icon-button" :type="type" :aria-label="label" :aria-pressed="pressed || undefined" :aria-expanded="expanded === undefined ? undefined : expanded" :disabled="disabled"><slot /></button></template>

<style>
.nxp-icon-button { min-width: 40px; min-height: 40px; }
</style>
