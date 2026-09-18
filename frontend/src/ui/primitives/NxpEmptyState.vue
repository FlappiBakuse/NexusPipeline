<script setup lang="ts">
withDefaults(defineProps<{ title?: string; description?: string; linkHref?: string; linkLabel?: string; tone?: "muted" | "danger" }>(), { title: "暂无内容", description: "", linkHref: "", linkLabel: "", tone: "muted" });
</script>

<template><div class="nxp-empty" :class="{ 'is-danger': tone === 'danger' }"><strong>{{ title }}<slot name="title" /></strong><span v-if="description || $slots.default">{{ description }}<slot v-if="$slots.default" /></span><a v-if="linkHref" class="back-link" :href="linkHref">{{ linkLabel }}</a></div></template>

<style>
:host { display: block; min-width: 0; }
.nxp-empty { padding: 48px 20px; border: 1px solid var(--content-card-border, var(--nx-color-border)); border-radius: var(--radius-lg, var(--nx-radius-lg)); background: var(--content-card, var(--nx-color-surface)); color: var(--muted, var(--nx-color-muted)); text-align: center; font-size: 13px; }
.nxp-empty strong { display: block; margin-bottom: 5px; color: var(--text, var(--nx-color-text)); font-size: 15px; }
.nxp-empty.is-danger { color: var(--bad, var(--nx-color-danger)); }
.nxp-empty.is-danger strong { color: var(--bad, var(--nx-color-danger)); }
</style>
