<script setup lang="ts">
import { computed } from "vue";
import { t } from "../../platform/i18n";

const props = withDefaults(defineProps<{ label?: string }>(), { label: "" });
// 公开元素在插件 surface 中可能先于宿主词典出现，因此保留调用点回退文案。
const text = computed(() => props.label || t("common.loading", {}, "Loading"));
</script>

<template><span class="nxp-spinner" role="status" :aria-label="text" /></template>

<style>
.nxp-spinner { display: inline-block; width: 20px; height: 20px; border: 2px solid color-mix(in srgb, var(--nx-color-accent) 25%, transparent); border-top-color: var(--nx-color-accent); border-radius: 50%; animation: nxp-spin .8s linear infinite; }
@keyframes nxp-spin { to { transform: rotate(360deg); } }
</style>
