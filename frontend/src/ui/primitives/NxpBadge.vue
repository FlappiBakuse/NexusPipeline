<script setup lang="ts">
withDefaults(defineProps<{ tone?: "muted" | "blue" | "ok" | "warn" | "bad" }>(), { tone: "muted" });
</script>

<template><span class="badge nxp-badge" :class="tone"><slot /></span></template>
