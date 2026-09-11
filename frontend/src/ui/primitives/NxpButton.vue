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
  <button class="nxp-button" :class="{ sm: size === 'sm', danger: tone === 'danger', ghost: variant === 'ghost' }" :type="type" :disabled="disabled"><slot /></button>
</template>

<style>
:host {
  display: inline-flex;
  min-width: 0;
  vertical-align: middle;
}
</style>
