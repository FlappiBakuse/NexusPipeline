<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { t } from "../../../platform/i18n";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpIconButton from "../../../ui/primitives/NxpIconButton.vue";

defineProps<{ image: { url: string; alt: string; caption: string } }>();
const emit = defineEmits<{ close: [] }>();
const stage = ref<HTMLElement | null>(null);
const closeButton = ref<InstanceType<typeof NxpIconButton> | null>(null);
const root = ref<HTMLElement | null>(null);
const size = ref({ width: 0, height: 0 });
const natural = ref({ width: 0, height: 0 });
const scale = ref(1);
const offset = ref({ x: 0, y: 0 });
const dragging = ref(false);
const pointers = new Map<number, { x: number; y: number }>();
let observer: ResizeObserver | undefined;
let returnFocus: HTMLElement | null = null;
const fitted = computed(() => {
  const ratio = Math.min(size.value.width / natural.value.width, size.value.height / natural.value.height);
  return Number.isFinite(ratio) ? { width: natural.value.width * ratio, height: natural.value.height * ratio } : { width: 0, height: 0 };
});
const imageStyle = computed(() => ({
  width: `${fitted.value.width}px`, height: `${fitted.value.height}px`,
  transform: `translate(${offset.value.x}px, ${offset.value.y}px) scale(${scale.value})`,
}));
function place(nextScale: number, x: number, y: number) {
  scale.value = Math.min(2, Math.max(.5, nextScale));
  const maxX = Math.max(0, (fitted.value.width * scale.value - size.value.width) / 2);
  const maxY = Math.max(0, (fitted.value.height * scale.value - size.value.height) / 2);
  offset.value = { x: Math.min(maxX, Math.max(-maxX, x)), y: Math.min(maxY, Math.max(-maxY, y)) };
}
function point(event: { clientX: number; clientY: number }) {
  const bounds = stage.value!.getBoundingClientRect();
  return { x: event.clientX - bounds.left - bounds.width / 2, y: event.clientY - bounds.top - bounds.height / 2 };
}
function zoom(value: number, anchor = { x: 0, y: 0 }) {
  const next = Math.min(2, Math.max(.5, value));
  const ratio = next / scale.value;
  place(next, anchor.x - (anchor.x - offset.value.x) * ratio, anchor.y - (anchor.y - offset.value.y) * ratio);
}
function wheel(event: WheelEvent) {
  const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? size.value.height : 1);
  zoom(scale.value * Math.exp(-Math.max(-300, Math.min(300, delta)) * .003), point(event));
}
function pointerDown(event: PointerEvent) {
  if ((event.pointerType === "mouse" && event.button !== 0) || pointers.size >= 2) return;
  stage.value!.setPointerCapture(event.pointerId);
  pointers.set(event.pointerId, point(event));
  dragging.value = true;
}
function pointerMove(event: PointerEvent) {
  const previous = pointers.get(event.pointerId);
  if (!previous) return;
  const before = [...pointers.values()];
  const current = point(event);
  pointers.set(event.pointerId, current);
  if (pointers.size === 1) {
    place(scale.value, offset.value.x + current.x - previous.x, offset.value.y + current.y - previous.y);
    return;
  }
  const after = [...pointers.values()];
  const distance = (points: typeof before) => Math.hypot(points[1].x - points[0].x, points[1].y - points[0].y);
  const middle = (points: typeof before) => ({ x: (points[0].x + points[1].x) / 2, y: (points[0].y + points[1].y) / 2 });
  const from = middle(before), to = middle(after);
  const next = Math.min(2, Math.max(.5, scale.value * distance(after) / Math.max(1, distance(before))));
  const ratio = next / scale.value;
  place(next, to.x - (from.x - offset.value.x) * ratio, to.y - (from.y - offset.value.y) * ratio);
}
function pointerEnd(event: PointerEvent) {
  pointers.delete(event.pointerId);
  dragging.value = pointers.size > 0;
}
function loaded(event: Event) {
  const image = event.target as HTMLImageElement;
  natural.value = { width: image.naturalWidth, height: image.naturalHeight };
  place(1, 0, 0);
}
function keydown(event: KeyboardEvent) {
  if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); emit("close"); }
  if (event.key !== "Tab") return;
  const buttons = [...(root.value?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") || [])];
  const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
  if (index < 0 || (event.shiftKey ? index === 0 : index === buttons.length - 1)) {
    event.preventDefault(); (event.shiftKey ? buttons.at(-1) : buttons[0])?.focus();
  }
}
onMounted(() => {
  returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  observer = new ResizeObserver(() => {
    size.value = { width: stage.value!.clientWidth, height: stage.value!.clientHeight };
    place(scale.value, offset.value.x, offset.value.y);
  });
  observer.observe(stage.value!);
  void nextTick(() => (closeButton.value?.$el as HTMLElement)?.focus({ preventScroll: true }));
  document.addEventListener("keydown", keydown, true);
});
onBeforeUnmount(() => {
  observer?.disconnect(); pointers.clear();
  document.removeEventListener("keydown", keydown, true);
  if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
});
</script>

<template>
  <Teleport to="body">
    <div ref="root" class="history-image-lightbox" role="dialog" aria-modal="true" :aria-label="t('history.view_run_screenshot')">
      <div class="history-image-lightbox-backdrop" @click="emit('close')"></div>
      <figure class="history-image-lightbox-content">
        <div class="history-image-toolbar">
          <NxpButton :disabled="scale <= .5" :aria-label="t('history.screenshot.zoom_out')" @click="zoom(scale - .1)">−</NxpButton>
          <NxpButton :aria-label="t('history.screenshot.zoom_reset')" @click="place(1, 0, 0)">{{ Math.round(scale * 100) }}%</NxpButton>
          <NxpButton :disabled="scale >= 2" :aria-label="t('history.screenshot.zoom_in')" @click="zoom(scale + .1)">+</NxpButton>
          <NxpIconButton ref="closeButton" class="history-image-lightbox-close" :label="t('history.screenshot.close')" @click="emit('close')"><NxpIcon name="close" /></NxpIconButton>
        </div>
        <div ref="stage" class="history-image-stage" :class="{ 'is-dragging': dragging }" @wheel.prevent="wheel" @pointerdown.prevent="pointerDown" @pointermove="pointerMove" @pointerup="pointerEnd" @pointercancel="pointerEnd" @lostpointercapture="pointerEnd">
          <img :src="image.url" :alt="image.alt" :style="imageStyle" draggable="false" @load="loaded">
        </div>
        <figcaption>{{ image.caption }}</figcaption>
      </figure>
    </div>
  </Teleport>
</template>
