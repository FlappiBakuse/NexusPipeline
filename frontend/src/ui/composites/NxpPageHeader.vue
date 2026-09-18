<script setup lang="ts">
import { useSlotPresence } from "../slots";
const hasSlot = useSlotPresence();
/** 页面级头部：统一 eyebrow、标题、说明与右侧 actions 的公开布局。
 *  调用方按页面需要填插槽。 */
const props = withDefaults(defineProps<{ eyebrow?: string; title?: string; description?: string }>(), {
  eyebrow: "",
  title: "",
  description: "",
});
</script>

<template>
  <header class="nxp-page-header" :class="{ 'has-actions': Boolean(hasSlot('actions')) }">
    <div class="nxp-page-header-copy">
      <div v-if="props.eyebrow || hasSlot('eyebrow')" class="nxp-page-header-eyebrow">
        <slot name="eyebrow">{{ props.eyebrow }}</slot>
      </div>
      <h2><slot name="title">{{ props.title }}</slot></h2>
      <p v-if="props.description || hasSlot('description')" class="nxp-page-header-kicker">
        <slot name="description">{{ props.description }}</slot>
      </p>
    </div>
    <div v-if="hasSlot('actions')" class="nxp-page-header-actions"><slot name="actions" /></div>
  </header>
</template>

<style>
.nxp-page-header { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: end; gap: var(--space-6); margin-bottom: var(--space-6); }
.nxp-page-header-copy { min-width: 0; }
.nxp-page-header-actions { display: flex; align-items: center; justify-content: flex-end; gap: var(--action-gap); }
.nxp-page-header-actions > button { min-width: 112px; }
.nxp-page-header .back-link { margin-bottom: 7px; }
.nxp-page-header-actions .back-link { margin-bottom: 0; }
.nxp-page-header h2 { margin: 0; }
.nxp-page-header-eyebrow { color: var(--faint); font-size: 11px; }
.nxp-page-header-kicker { max-width: 72ch; margin: 7px 0 0; color: var(--muted); font-size: 12px; }

@media (max-width: 600px) {
  .nxp-page-header { grid-template-columns: minmax(0, 1fr); align-items: start; gap: var(--space-4); margin-bottom: var(--space-5); }
  .nxp-page-header-actions { width: 100%; justify-content: stretch; }
  .nxp-page-header-actions > button,
  .nxp-page-header-actions > a { width: 100%; min-width: 0; }
}
</style>
