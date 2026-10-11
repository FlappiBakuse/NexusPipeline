<script setup lang="ts">
import { nextTick, onMounted, onBeforeUnmount, ref } from "vue";
import { renderDashboardCard, disposeDashboardCard, onDashboardRenderersChanged, hasDashboardRenderer } from "@bridge/index";
const props = defineProps<{ cardId: string }>();
const element = ref<HTMLElement | null>(null);
let unsubscribe: (() => void) | undefined;
const ready = ref(hasDashboardRenderer(props.cardId));
let disposed = false;
async function render() {
  ready.value = hasDashboardRenderer(props.cardId);
  await nextTick();
  if (!disposed && element.value) void renderDashboardCard(element.value, props.cardId);
}
onMounted(() => { unsubscribe = onDashboardRenderersChanged(render); render(); });
onBeforeUnmount(() => { disposed = true; unsubscribe?.(); if (element.value) disposeDashboardCard(element.value); });
</script>
<template><section v-if="ready" ref="element" :data-dashboard-card="cardId" /></template>
