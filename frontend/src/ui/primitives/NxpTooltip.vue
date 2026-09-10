<script setup lang="ts">
withDefaults(defineProps<{ text?: string; placement?: "top" | "right" | "bottom" | "left" }>(), { text: "", placement: "top" });
</script>

<template>
  <span class="nxp-tooltip" :class="`is-${placement}`"><slot /><span v-if="text" class="nxp-tooltip-content" role="tooltip">{{ text }}</span></span>
</template>

<style>
.nxp-tooltip { position: relative; display: inline-flex; }
.nxp-tooltip-content { position: absolute; z-index: 20; display: none; max-width: 240px; padding: var(--nx-space-2) var(--nx-space-3); border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); background: var(--nx-color-surface); color: var(--nx-color-text); font-size: 12px; line-height: 1.4; box-shadow: var(--shadow, 0 12px 28px rgba(0, 0, 0, .24)); white-space: normal; }
.nxp-tooltip:hover .nxp-tooltip-content, .nxp-tooltip:focus-within .nxp-tooltip-content { display: block; }
.nxp-tooltip.is-top .nxp-tooltip-content { right: 50%; bottom: calc(100% + var(--nx-space-2)); transform: translateX(50%); }
.nxp-tooltip.is-right .nxp-tooltip-content { top: 50%; left: calc(100% + var(--nx-space-2)); transform: translateY(-50%); }
.nxp-tooltip.is-bottom .nxp-tooltip-content { top: calc(100% + var(--nx-space-2)); right: 50%; transform: translateX(50%); }
.nxp-tooltip.is-left .nxp-tooltip-content { top: 50%; right: calc(100% + var(--nx-space-2)); transform: translateY(-50%); }
</style>
