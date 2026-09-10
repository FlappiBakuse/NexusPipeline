<script setup lang="ts">
withDefaults(defineProps<{ message?: string; tone?: "info" | "success" | "warning" | "danger"; visible?: boolean }>(), { message: "", tone: "info", visible: true });
</script>

<template><div v-if="visible && message" class="nxp-toast" :class="`is-${tone}`" role="status" aria-live="polite"><slot>{{ message }}</slot></div></template>

<style>
.nxp-toast { border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); padding: var(--nx-space-3) var(--nx-space-4); background: var(--nx-color-surface); color: var(--nx-color-text); box-shadow: var(--shadow, 0 12px 28px rgba(0, 0, 0, .24)); }
.nxp-toast.is-success { border-color: var(--success, var(--nx-color-border)); }
.nxp-toast.is-warning { border-color: var(--warning, var(--nx-color-border)); }
.nxp-toast.is-danger { border-color: var(--nx-color-danger); }
</style>
