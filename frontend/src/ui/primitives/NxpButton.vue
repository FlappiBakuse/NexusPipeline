<script setup lang="ts">
withDefaults(defineProps<{
  tone?: "default" | "primary" | "success" | "warning" | "danger";
  variant?: "solid" | "ghost";
  size?: "sm" | "md";
  type?: "button" | "submit" | "reset";
  disabled?: boolean;
}>(), { tone: "default", variant: "solid", size: "md", type: "button", disabled: false });
</script>

<template>
  <button class="nxp-button" :class="[`is-${tone}`, `is-${variant}`, `is-${size}`]" :type="type" :disabled="disabled"><slot /></button>
</template>

<style>
.nxp-button { min-height: var(--nx-control-height); border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); padding: 0 var(--nx-space-4); background: var(--nx-color-accent); color: var(--on-accent, #08111f); font: inherit; cursor: pointer; transition: filter .16s ease, opacity .16s ease; }
.nxp-button:hover:not(:disabled) { filter: brightness(1.08); }
.nxp-button:disabled { opacity: .5; cursor: not-allowed; }
.nxp-button.is-ghost { background: transparent; color: var(--nx-color-text); }
.nxp-button.is-success { background: var(--success, #68d391); }
.nxp-button.is-warning { background: var(--warning, #e9b949); }
.nxp-button.is-danger { background: var(--nx-color-danger); }
.nxp-button.is-sm { min-height: 36px; padding-inline: var(--nx-space-3); font-size: 12px; }
</style>
