<script setup lang="ts">
withDefaults(defineProps<{ tone?: "default" | "secondary" }>(), { tone: "default" });
</script>

<template><section class="nxp-card" :class="`is-${tone}`"><slot /></section></template>

<style>
.nxp-card { min-width: 0; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-lg); padding: var(--nx-space-5); background: var(--nx-color-surface); color: var(--nx-color-text); }
.nxp-card.is-secondary { background: var(--secondary-surface, var(--nx-color-surface)); }
</style>
