<script setup lang="ts">
withDefaults(defineProps<{ title?: string; description?: string; linkHref?: string; linkLabel?: string; tone?: "muted" | "danger" }>(), { title: "暂无内容", description: "", linkHref: "", linkLabel: "", tone: "muted" });
</script>

<template><div class="nxp-empty" :class="`is-${tone}`"><strong>{{ title }}<slot name="title" /></strong><span v-if="description || $slots.default">{{ description }}<slot v-if="$slots.default" /></span><a v-if="linkHref" class="back-link" :href="linkHref">{{ linkLabel }}</a></div></template>

<style>
.nxp-empty { display: grid; gap: var(--nx-space-2); place-items: center; min-height: 120px; padding: var(--nx-space-5); color: var(--nx-color-muted); text-align: center; }
.nxp-empty strong { color: var(--nx-color-text); }
.nxp-empty.is-danger strong { color: var(--nx-color-danger); }
</style>
