<script setup lang="ts">
withDefaults(defineProps<{ tone?: "muted" | "blue" | "ok" | "warn" | "bad" }>(), { tone: "muted" });
</script>

<template><span class="nxp-badge" :class="`is-${tone}`"><slot /></span></template>

<style>
.nxp-badge { display: inline-flex; min-height: 24px; align-items: center; border-radius: 999px; padding: 2px 9px; background: color-mix(in srgb, var(--nx-color-muted) 18%, transparent); color: var(--nx-color-muted); font-size: 11px; }
.nxp-badge.is-blue { color: var(--info, #76a7ff); background: color-mix(in srgb, var(--info, #76a7ff) 16%, transparent); }
.nxp-badge.is-ok { color: var(--success, #68d391); background: color-mix(in srgb, var(--success, #68d391) 16%, transparent); }
.nxp-badge.is-warn { color: var(--warning, #e9b949); background: color-mix(in srgb, var(--warning, #e9b949) 16%, transparent); }
.nxp-badge.is-bad { color: var(--nx-color-danger); background: color-mix(in srgb, var(--nx-color-danger) 16%, transparent); }
</style>
