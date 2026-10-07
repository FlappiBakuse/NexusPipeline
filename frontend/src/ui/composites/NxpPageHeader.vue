<script setup lang="ts">
import { useSlotPresence } from "../slots";
const hasSlot = useSlotPresence();
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
.nxp-page-header-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: var(--action-gap); }
.nxp-page-header-actions > button { min-width: 112px; }
.nxp-page-header .back-link { margin-bottom: 7px; }
.nxp-page-header-actions .back-link { margin-bottom: 0; }
.nxp-page-header h2 { margin: 0; }
.nxp-page-header-eyebrow { color: var(--faint); font-size: 11px; }
.nxp-page-header-kicker { max-width: 72ch; margin: 7px 0 0; color: var(--muted); font-size: 12px; }

@media (max-width: 600px) {
  .nxp-page-header { align-items: end; gap: var(--space-4); margin-bottom: var(--space-5); }
  .nxp-page-header-actions { min-width: 0; max-width: 50vw; justify-self: end; align-self: end; }
  .nxp-page-header-actions > button,
  .nxp-page-header-actions > a { min-width: 0; max-width: 100%; }
}
</style>
