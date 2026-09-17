<script setup lang="ts">
import { useSlotPresence } from "../slots";
const hasSlot = useSlotPresence();
const props = withDefaults(defineProps<{
  itemId?: string;
  as?: "div" | "article" | "li";
  slotLayout?: "normal" | "contents";
}>(), {
  itemId: "",
  as: "div",
  slotLayout: "normal",
});
</script>

<template>
  <component
    :is="props.as"
    class="nxp-entity-row"
    :data-dnd-id="props.itemId || undefined"
    data-entity-row
  >
    <div v-if="hasSlot('leading')" class="nxp-entity-row-leading" :class="{ 'is-contents': props.slotLayout === 'contents' }"><slot name="leading" /></div>
    <div class="nxp-entity-row-content"><slot name="content"><slot /></slot></div>
    <div v-if="hasSlot('meta')" class="nxp-entity-row-meta"><slot name="meta" /></div>
    <div v-if="hasSlot('actions')" class="nxp-entity-row-actions" :class="{ 'is-contents': props.slotLayout === 'contents' }"><slot name="actions" /></div>
  </component>
</template>

<style>
.nxp-entity-row { display: grid; min-width: 0; align-items: center; gap: var(--nx-space-3, 12px); }
.nxp-entity-row-leading,
.nxp-entity-row-actions { display: flex; min-width: 0; align-items: center; gap: var(--nx-action-gap, 8px); }
.nxp-entity-row-leading.is-contents,
.nxp-entity-row-actions.is-contents { display: contents; }
.nxp-entity-row-content { min-width: 0; }
.nxp-entity-row-meta { display: flex; min-width: 0; flex-wrap: wrap; align-items: center; gap: var(--nx-space-2, 8px); }
</style>
