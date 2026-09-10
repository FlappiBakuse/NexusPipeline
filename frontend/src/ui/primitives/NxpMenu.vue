<script setup lang="ts">
import { ref, watch } from "vue";

const props = withDefaults(defineProps<{ open?: boolean; label?: string; disabled?: boolean }>(), { open: false, label: "菜单", disabled: false });
const emit = defineEmits<{ "update:open": [value: boolean]; close: [] }>();
const expanded = ref(props.open);
watch(() => props.open, value => { expanded.value = value; });

function toggle() {
  if (props.disabled) return;
  expanded.value = !expanded.value;
  emit("update:open", expanded.value);
}

function close() {
  expanded.value = false;
  emit("update:open", false);
  emit("close");
}
</script>

<template>
  <div class="nxp-menu" @keydown.esc="close">
    <button class="nxp-menu-trigger" type="button" :aria-label="props.label" :aria-expanded="expanded" :disabled="props.disabled" @click="toggle"><slot name="trigger">{{ props.label }}</slot></button>
    <div v-if="expanded" class="nxp-menu-popup" role="menu" @click="close"><slot /></div>
  </div>
</template>

<style>
.nxp-menu { position: relative; display: inline-flex; }
.nxp-menu-trigger { min-height: var(--nx-control-height); border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); padding: 0 var(--nx-space-3); background: transparent; color: var(--nx-color-text); font: inherit; cursor: pointer; }
.nxp-menu-popup { position: absolute; z-index: 10; top: calc(100% + var(--nx-space-1)); right: 0; min-width: 160px; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); padding: var(--nx-space-1); background: var(--nx-color-surface); box-shadow: var(--shadow, 0 12px 28px rgba(0, 0, 0, .24)); }
</style>
