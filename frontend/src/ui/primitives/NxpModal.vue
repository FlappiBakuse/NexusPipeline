<script setup lang="ts">
import NxpIcon from "./NxpIcon.vue";

const props = withDefaults(defineProps<{ open?: boolean; title?: string; closeable?: boolean }>(), { open: false, title: "", closeable: true });
const emit = defineEmits<{ close: [] }>();
</script>

<template>
  <div v-if="props.open" class="nxp-modal" role="dialog" aria-modal="true" :aria-label="props.title || undefined">
    <button v-if="props.closeable" class="nxp-modal-backdrop" type="button" aria-label="关闭" @click="emit('close')"></button>
    <section class="nxp-modal-panel">
      <header v-if="title || closeable" class="nxp-modal-header"><h2 v-if="title">{{ title }}</h2><button v-if="closeable" type="button" aria-label="关闭" @click="emit('close')"><NxpIcon name="close" /></button></header>
      <div class="nxp-modal-body"><slot /></div>
      <footer v-if="$slots.footer" class="nxp-modal-footer"><slot name="footer" /></footer>
    </section>
  </div>
</template>

<style>
.nxp-modal { position: fixed; inset: 0; z-index: 100; display: grid; place-items: center; padding: var(--nx-space-5); }
.nxp-modal-backdrop { position: absolute; inset: 0; border: 0; background: var(--mask, rgba(0, 0, 0, .5)); cursor: default; }
.nxp-modal-panel { position: relative; z-index: 1; width: min(560px, 100%); max-height: min(90vh, 720px); overflow: auto; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-lg); background: var(--nx-color-surface); color: var(--nx-color-text); box-shadow: var(--shadow, 0 16px 40px rgba(0, 0, 0, .28)); }
.nxp-modal-header, .nxp-modal-footer { display: flex; align-items: center; justify-content: space-between; gap: var(--nx-space-3); padding: var(--nx-space-4) var(--nx-space-5); border-bottom: 1px solid var(--nx-color-border); }
.nxp-modal-header h2 { margin: 0; font-size: 16px; }
.nxp-modal-header button { display: inline-flex; min-width: 36px; min-height: 36px; align-items: center; justify-content: center; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-md); background: transparent; color: var(--nx-color-muted, var(--nx-color-text)); cursor: pointer; }
.nxp-modal-header button:hover { background: var(--nx-color-surface-hover, transparent); color: var(--nx-color-text); }
.nxp-modal-header button .nxp-icon { width: 18px; height: 18px; }
.nxp-modal-body { padding: var(--nx-space-5); }
.nxp-modal-footer { justify-content: flex-end; border-top: 1px solid var(--nx-color-border); border-bottom: 0; }
</style>
