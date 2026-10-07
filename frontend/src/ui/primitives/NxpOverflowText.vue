<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
const props = withDefaults(defineProps<{ label?: string; wrap?: boolean }>(), { label: "", wrap: false });
const root = ref<HTMLElement | null>(null);
const inner = ref<HTMLElement | null>(null);
const overflowing = ref(false);
const fullText = ref("");
const insideControl = ref(false);
let resizeObserver: ResizeObserver | null = null;
let contentObserver: MutationObserver | null = null;
let frame = 0;
let disposed = false;
function measure() {
  frame = 0;
  if (!root.value || !inner.value) return;
  const distance = props.wrap ? 0 : Math.max(0, inner.value.scrollWidth - root.value.clientWidth);
  overflowing.value = distance > 1;
  insideControl.value = Boolean(root.value.closest("button, a, [role=button]"));
  fullText.value = inner.value.textContent?.trim() || "";
  root.value.style.setProperty("--nx-overflow-distance", `${-distance}px`);
  root.value.style.setProperty("--nx-overflow-duration", `${Math.max(3, distance / 28)}s`);
}
function schedule() { if (!disposed && !frame) frame = requestAnimationFrame(measure); }
watch([() => props.label, () => props.wrap], async () => { await nextTick(); schedule(); });
onMounted(() => {
  if (typeof ResizeObserver !== "undefined") {
    resizeObserver = new ResizeObserver(schedule);
    if (root.value) resizeObserver.observe(root.value);
    if (inner.value) resizeObserver.observe(inner.value);
  }
  if (inner.value && typeof MutationObserver !== "undefined") {
    contentObserver = new MutationObserver(schedule);
    contentObserver.observe(inner.value, { childList: true, characterData: true, subtree: true });
  }
  void document.fonts?.ready.then(schedule);
  schedule();
});
onBeforeUnmount(() => { disposed = true; resizeObserver?.disconnect(); contentObserver?.disconnect(); if (frame) cancelAnimationFrame(frame); });
</script>

<template>
  <span ref="root" class="nxp-overflow-text" :class="{ 'is-overflowing': overflowing, 'is-wrapping': wrap }" :title="overflowing ? fullText : undefined" :tabindex="overflowing && !insideControl ? 0 : undefined">
    <span ref="inner" class="nxp-overflow-text-inner"><slot>{{ props.label }}</slot></span>
  </span>
</template>

<style>
nxp-overflow-text { display: inline-block; min-width: 0; max-width: 100%; vertical-align: middle; }
.nxp-overflow-text { display: block; min-width: 0; max-width: 100%; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.nxp-overflow-text-inner { display: inline-block; min-width: 0; max-width: 100%; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; vertical-align: bottom; }
.nxp-overflow-text.is-wrapping, .nxp-overflow-text.is-wrapping > .nxp-overflow-text-inner { white-space: normal; overflow-wrap: anywhere; text-overflow: clip; }
.nxp-overflow-text.is-overflowing:is(:hover, :focus, :focus-within) > .nxp-overflow-text-inner,
:is(button, a, [role="button"]):is(:hover, :focus-visible) .nxp-overflow-text.is-overflowing > .nxp-overflow-text-inner { max-width: none; overflow: visible; text-overflow: clip; animation: nxp-overflow-scroll var(--nx-overflow-duration, 5s) .6s ease-in-out infinite alternate; }
:is(button, a, [role="button"]) .nxp-overflow-text { outline: none; }
@keyframes nxp-overflow-scroll { 0%, 12% { transform: translateX(0); } 88%, 100% { transform: translateX(var(--nx-overflow-distance, 0px)); } }
@media (prefers-reduced-motion: reduce) { .nxp-overflow-text > .nxp-overflow-text-inner { max-width: 100% !important; overflow: hidden !important; text-overflow: ellipsis !important; animation: none !important; } }
</style>
