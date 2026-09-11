<script setup lang="ts">
withDefaults(defineProps<{ title?: string; description?: string; linkHref?: string; linkLabel?: string; tone?: "muted" | "danger" }>(), { title: "暂无内容", description: "", linkHref: "", linkLabel: "", tone: "muted" });
</script>

<template><div class="empty nxp-empty" :class="{ 'is-danger': tone === 'danger' }"><strong>{{ title }}<slot name="title" /></strong><span v-if="description || $slots.default">{{ description }}<slot v-if="$slots.default" /></span><a v-if="linkHref" class="back-link" :href="linkHref">{{ linkLabel }}</a></div></template>
