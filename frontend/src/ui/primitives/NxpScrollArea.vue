<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, useAttrs } from "vue";

defineOptions({ inheritAttrs: false });

type ScrollDirection = "vertical" | "horizontal" | "both";
type ScrollAxis = "vertical" | "horizontal";

const attrs = useAttrs();
const props = withDefaults(defineProps<{
  direction?: ScrollDirection;
  ariaLabel?: string;
}>(), {
  direction: "vertical",
  ariaLabel: "",
});

const shell = ref<HTMLElement | null>(null);
const viewport = ref<HTMLElement | null>(null);
const metrics = reactive({
  vertical: false,
  horizontal: false,
  verticalThumb: 0,
  verticalOffset: 0,
  horizontalThumb: 0,
  horizontalOffset: 0,
  verticalValue: 0,
  horizontalValue: 0,
});
const scrollVisible = ref(false);
let hideTimer: ReturnType<typeof setTimeout> | null = null;
let frame: number | null = null;
let resizeObserver: ResizeObserver | null = null;
let mutationObserver: MutationObserver | null = null;
let unbindWindow: (() => void) | null = null;
let drag: { axis: ScrollAxis; pointer: number; scroll: number; track: number; thumb: number } | null = null;

const generatedViewportId = `nxp-scroll-viewport-${Math.random().toString(36).slice(2)}`;
const viewportId = computed(() => String(attrs.id || generatedViewportId));
const hasVertical = computed(() => props.direction === "vertical" || props.direction === "both");
const hasHorizontal = computed(() => props.direction === "horizontal" || props.direction === "both");
const rootAttrs = computed(() => {
  const {
    class: _class,
    id: _id,
    role: _role,
    tabindex: _tabindex,
    style: _style,
    "aria-label": _ariaLabel,
    "data-testid": _testId,
    ...rest
  } = attrs;
  return { ...rest, "data-testid": attrs["data-testid"] };
});
const viewportAttrs = computed(() => {
  const {
    class: _class,
    style: _style,
    id: _id,
    tabindex: _tabindex,
    "data-testid": _testId,
    ...rest
  } = attrs;
  return rest;
});

function clearHideTimer() {
  if (hideTimer !== null) {
    clearTimeout(hideTimer);
    hideTimer = null;
  }
}

function showScrollbars() {
  scrollVisible.value = true;
  clearHideTimer();
  hideTimer = setTimeout(() => {
    if (!drag) scrollVisible.value = false;
  }, 1200);
}

function updateMetrics() {
  const element = viewport.value;
  if (!element) return;
  const verticalRange = Math.max(0, element.scrollHeight - element.clientHeight);
  const horizontalRange = Math.max(0, element.scrollWidth - element.clientWidth);
  const vertical = hasVertical.value && verticalRange > 1;
  const horizontal = hasHorizontal.value && horizontalRange > 1;
  const verticalTrack = Math.max(1, element.clientHeight - 8);
  const horizontalTrack = Math.max(1, element.clientWidth - 8);
  const verticalThumb = vertical
    ? Math.max(24, Math.round(verticalTrack * element.clientHeight / Math.max(element.scrollHeight, 1)))
    : 0;
  const horizontalThumb = horizontal
    ? Math.max(24, Math.round(horizontalTrack * element.clientWidth / Math.max(element.scrollWidth, 1)))
    : 0;
  metrics.vertical = vertical;
  metrics.horizontal = horizontal;
  metrics.verticalThumb = Math.min(verticalTrack, verticalThumb);
  metrics.horizontalThumb = Math.min(horizontalTrack, horizontalThumb);
  metrics.verticalOffset = vertical
    ? Math.round((verticalTrack - metrics.verticalThumb) * element.scrollTop / verticalRange)
    : 0;
  metrics.horizontalOffset = horizontal
    ? Math.round((horizontalTrack - metrics.horizontalThumb) * element.scrollLeft / horizontalRange)
    : 0;
  metrics.verticalValue = element.scrollTop;
  metrics.horizontalValue = element.scrollLeft;
}

function scheduleMetrics() {
  if (frame !== null) return;
  frame = requestAnimationFrame(() => {
    frame = null;
    updateMetrics();
  });
}

function onScroll() {
  showScrollbars();
  scheduleMetrics();
}

function scrollToAxis(axis: ScrollAxis, value: number) {
  const element = viewport.value;
  if (!element) return;
  if (axis === "vertical") element.scrollTop = Math.max(0, value);
  else element.scrollLeft = Math.max(0, value);
  showScrollbars();
  scheduleMetrics();
}

function onThumbPointerDown(event: PointerEvent, axis: ScrollAxis) {
  const element = viewport.value;
  const thumb = event.currentTarget as HTMLElement | null;
  if (!element || !thumb) return;
  event.preventDefault();
  event.stopPropagation();
  const isVertical = axis === "vertical";
  drag = {
    axis,
    pointer: isVertical ? event.clientY : event.clientX,
    scroll: isVertical ? element.scrollTop : element.scrollLeft,
    track: isVertical ? Math.max(1, element.clientHeight - 8) : Math.max(1, element.clientWidth - 8),
    thumb: isVertical ? metrics.verticalThumb : metrics.horizontalThumb,
  };
  thumb.setPointerCapture?.(event.pointerId);
  showScrollbars();
}

function onPointerMove(event: PointerEvent) {
  if (!drag) return;
  const element = viewport.value;
  if (!element) return;
  const isVertical = drag.axis === "vertical";
  const pointer = isVertical ? event.clientY : event.clientX;
  const range = isVertical
    ? Math.max(0, element.scrollHeight - element.clientHeight)
    : Math.max(0, element.scrollWidth - element.clientWidth);
  const travel = Math.max(1, drag.track - drag.thumb);
  const next = drag.scroll + (pointer - drag.pointer) * range / travel;
  scrollToAxis(drag.axis, next);
}

function onPointerUp() {
  if (!drag) return;
  drag = null;
  showScrollbars();
}

function onScrollbarKeydown(event: KeyboardEvent, axis: ScrollAxis) {
  const element = viewport.value;
  if (!element) return;
  const isVertical = axis === "vertical";
  const current = isVertical ? element.scrollTop : element.scrollLeft;
  const page = isVertical ? element.clientHeight : element.clientWidth;
  const max = isVertical ? element.scrollHeight - element.clientHeight : element.scrollWidth - element.clientWidth;
  let next: number | null = null;
  if ((isVertical && event.key === "ArrowDown") || (!isVertical && event.key === "ArrowRight")) next = current + 48;
  if ((isVertical && event.key === "ArrowUp") || (!isVertical && event.key === "ArrowLeft")) next = current - 48;
  if (event.key === "PageDown") next = current + page;
  if (event.key === "PageUp") next = current - page;
  if (event.key === "Home") next = 0;
  if (event.key === "End") next = max;
  if (next === null) return;
  event.preventDefault();
  scrollToAxis(axis, Math.max(0, Math.min(max, next)));
}

onMounted(async () => {
  const element = viewport.value;
  if (!element) return;
  await nextTick();
  updateMetrics();
  element.addEventListener("scroll", onScroll, { passive: true });
  const onResize = () => scheduleMetrics();
  window.addEventListener("resize", onResize);
  unbindWindow = () => {
    element.removeEventListener("scroll", onScroll);
    window.removeEventListener("resize", onResize);
  };
  if (typeof ResizeObserver !== "undefined") {
    resizeObserver = new ResizeObserver(scheduleMetrics);
    resizeObserver.observe(element);
  }
  if (typeof MutationObserver !== "undefined") {
    mutationObserver = new MutationObserver(scheduleMetrics);
    mutationObserver.observe(element, { childList: true, subtree: true, characterData: true, attributes: true });
  }
  window.addEventListener("pointermove", onPointerMove);
  window.addEventListener("pointerup", onPointerUp);
});

onBeforeUnmount(() => {
  clearHideTimer();
  if (frame !== null) cancelAnimationFrame(frame);
  resizeObserver?.disconnect();
  mutationObserver?.disconnect();
  unbindWindow?.();
  window.removeEventListener("pointermove", onPointerMove);
  window.removeEventListener("pointerup", onPointerUp);
});
</script>

<template>
  <div ref="shell" v-bind="rootAttrs" class="nxp-scroll-area" :style="attrs.style" :class="[attrs.class, { 'is-scroll-visible': scrollVisible, 'has-vertical': metrics.vertical, 'has-horizontal': metrics.horizontal }]">
    <div
      ref="viewport"
      v-bind="viewportAttrs"
      class="nxp-scroll-viewport"
      :id="viewportId"
      :tabindex="String(attrs.tabindex ?? 0)"
      :aria-label="props.ariaLabel || undefined"
      @pointerenter="showScrollbars"
      @focusin="showScrollbars"
    >
      <slot />
    </div>
    <div v-if="metrics.vertical" class="nxp-scroll-track nxp-scroll-track-vertical">
      <div
        class="nxp-scroll-thumb"
        :style="{ height: `${metrics.verticalThumb}px`, transform: `translateY(${metrics.verticalOffset}px)` }"
        role="scrollbar"
        tabindex="0"
        aria-orientation="vertical"
        :aria-controls="viewportId"
        aria-valuemin="0"
        :aria-valuemax="100"
        :aria-valuenow="Math.round(metrics.verticalValue / Math.max(1, (viewport?.scrollHeight || 1) - (viewport?.clientHeight || 1)) * 100)"
        @pointerdown="onThumbPointerDown($event, 'vertical')"
        @keydown="onScrollbarKeydown($event, 'vertical')"
      />
    </div>
    <div v-if="metrics.horizontal" class="nxp-scroll-track nxp-scroll-track-horizontal">
      <div
        class="nxp-scroll-thumb"
        :style="{ width: `${metrics.horizontalThumb}px`, transform: `translateX(${metrics.horizontalOffset}px)` }"
        role="scrollbar"
        tabindex="0"
        aria-orientation="horizontal"
        :aria-controls="viewportId"
        aria-valuemin="0"
        aria-valuemax="100"
        :aria-valuenow="Math.round(metrics.horizontalValue / Math.max(1, (viewport?.scrollWidth || 1) - (viewport?.clientWidth || 1)) * 100)"
        @pointerdown="onThumbPointerDown($event, 'horizontal')"
        @keydown="onScrollbarKeydown($event, 'horizontal')"
      />
    </div>
  </div>
</template>

<style>
.nxp-scroll-area { position: relative; display: flex; min-width: 0; min-height: 0; flex: 1 1 auto; flex-direction: column; overflow: hidden; }
.nxp-scroll-viewport { min-width: 0; min-height: 0; flex: 1 1 auto; overflow: auto; overscroll-behavior: contain; scrollbar-width: none; -ms-overflow-style: none; outline: none; }
.nxp-scroll-viewport::-webkit-scrollbar { width: 0; height: 0; }
.nxp-scroll-track { position: absolute; z-index: 2; pointer-events: none; opacity: 0; transition: opacity var(--nx-motion-duration-fast, .15s) var(--nx-motion-ease, ease); }
.nxp-scroll-track-vertical { top: 4px; right: 2px; bottom: 4px; width: 8px; }
.nxp-scroll-track-horizontal { right: 4px; bottom: 2px; left: 4px; height: 8px; }
.nxp-scroll-area:hover .nxp-scroll-track,
.nxp-scroll-area:focus-within .nxp-scroll-track,
.nxp-scroll-area.is-scroll-visible .nxp-scroll-track { opacity: 1; }
.nxp-scroll-thumb { position: absolute; box-sizing: border-box; min-width: 8px; min-height: 8px; border: 0; border-radius: 999px; background: color-mix(in srgb, var(--nx-color-text, #7d8798) 48%, transparent); cursor: grab; pointer-events: auto; }
.nxp-scroll-thumb:hover, .nxp-scroll-thumb:focus-visible { background: color-mix(in srgb, var(--nx-color-text, #7d8798) 68%, transparent); outline: none; }
.nxp-scroll-thumb:active { cursor: grabbing; }
.nxp-scroll-track-vertical .nxp-scroll-thumb { top: 0; right: 0; width: 100%; }
.nxp-scroll-track-horizontal .nxp-scroll-thumb { bottom: 0; left: 0; height: 100%; }
@media (prefers-reduced-motion: reduce) { .nxp-scroll-track { transition: none; } }
</style>
