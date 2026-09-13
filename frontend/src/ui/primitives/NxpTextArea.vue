<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, useAttrs, watch } from "vue";

defineOptions({ inheritAttrs: false });

const attrs = useAttrs();
const props = withDefaults(defineProps<{
  id?: string;
  modelValue?: string;
  placeholder?: string;
  disabled?: boolean;
  rows?: number;
  maxlength?: number | string;
  readonly?: boolean;
  ariaLabel?: string;
}>(), {
  id: undefined,
  modelValue: "",
  placeholder: "",
  disabled: false,
  rows: 4,
  maxlength: undefined,
  readonly: false,
  ariaLabel: undefined,
});
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();

const shell = ref<HTMLElement | null>(null);
const input = ref<HTMLTextAreaElement | null>(null);
const metrics = reactive({ visible: false, thumb: 0, offset: 0, value: 0 });
const scrollVisible = ref(false);
let hideTimer: ReturnType<typeof setTimeout> | null = null;
let frame: number | null = null;
let resizeObserver: ResizeObserver | null = null;
let mutationObserver: MutationObserver | null = null;
let unbindWindow: (() => void) | null = null;
let drag: { pointer: number; scroll: number; track: number; thumb: number } | null = null;

const generatedInputId = `nxp-textarea-${Math.random().toString(36).slice(2)}`;
const inputId = computed(() => props.id || String(attrs.id || generatedInputId));
const ariaLabel = computed(() => props.ariaLabel || (typeof attrs["aria-label"] === "string" ? attrs["aria-label"] : undefined));
const inputAttrs = computed(() => {
  const {
    class: _class,
    style: _style,
    id: _id,
    "aria-label": _ariaLabel,
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
  const element = input.value;
  if (!element) return;
  const range = Math.max(0, element.scrollHeight - element.clientHeight);
  const track = Math.max(1, element.clientHeight - 12);
  const visible = range > 1;
  const thumb = visible
    ? Math.max(24, Math.round(track * element.clientHeight / Math.max(element.scrollHeight, 1)))
    : 0;
  metrics.visible = visible;
  metrics.thumb = Math.min(track, thumb);
  metrics.offset = visible
    ? Math.round((track - metrics.thumb) * element.scrollTop / range)
    : 0;
  metrics.value = element.scrollTop;
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

function scrollTo(value: number) {
  const element = input.value;
  if (!element) return;
  const max = Math.max(0, element.scrollHeight - element.clientHeight);
  element.scrollTop = Math.max(0, Math.min(max, value));
  showScrollbars();
  scheduleMetrics();
}

function onThumbPointerDown(event: PointerEvent) {
  const element = input.value;
  const thumb = event.currentTarget as HTMLElement | null;
  if (!element || !thumb) return;
  event.preventDefault();
  event.stopPropagation();
  drag = {
    pointer: event.clientY,
    scroll: element.scrollTop,
    track: Math.max(1, element.clientHeight - 12),
    thumb: metrics.thumb,
  };
  thumb.setPointerCapture?.(event.pointerId);
  showScrollbars();
}

function onPointerMove(event: PointerEvent) {
  if (!drag) return;
  const element = input.value;
  if (!element) return;
  const range = Math.max(0, element.scrollHeight - element.clientHeight);
  const travel = Math.max(1, drag.track - drag.thumb);
  scrollTo(drag.scroll + (event.clientY - drag.pointer) * range / travel);
}

function onPointerUp() {
  if (!drag) return;
  drag = null;
  showScrollbars();
}

function onScrollbarKeydown(event: KeyboardEvent) {
  const element = input.value;
  if (!element) return;
  const current = element.scrollTop;
  const page = element.clientHeight;
  const max = Math.max(0, element.scrollHeight - element.clientHeight);
  let next: number | null = null;
  if (event.key === "ArrowDown") next = current + 48;
  if (event.key === "ArrowUp") next = current - 48;
  if (event.key === "PageDown") next = current + page;
  if (event.key === "PageUp") next = current - page;
  if (event.key === "Home") next = 0;
  if (event.key === "End") next = max;
  if (next === null) return;
  event.preventDefault();
  scrollTo(next);
}

function update(event: Event) {
  emit("update:modelValue", (event.target as HTMLTextAreaElement).value);
  scheduleMetrics();
}
function change(event: Event) {
  emit("change", (event.target as HTMLTextAreaElement).value);
  scheduleMetrics();
}

watch(() => props.modelValue, () => {
  void nextTick(scheduleMetrics);
});

onMounted(async () => {
  const element = input.value;
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
    mutationObserver.observe(element, { attributes: true, childList: true, characterData: true, subtree: true });
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

defineExpose({ focus: () => input.value?.focus() });
</script>

<template>
  <div
    ref="shell"
    class="nxp-textarea-shell"
    :class="{ 'is-scroll-visible': scrollVisible, 'has-scroll': metrics.visible }"
    @pointerenter="showScrollbars"
    @focusin="showScrollbars"
  >
    <textarea
      ref="input"
      v-bind="inputAttrs"
      :class="[attrs.class, 'nxp-input', 'nxp-textarea']"
      :style="attrs.style"
      :id="inputId"
      :value="props.modelValue"
      :placeholder="props.placeholder"
      :disabled="props.disabled"
      :rows="props.rows"
      :maxlength="props.maxlength"
      :readonly="props.readonly"
      :aria-label="ariaLabel"
      @input.stop="update"
      @change.stop="change"
    />
    <div v-if="metrics.visible" class="nxp-textarea-scroll-track">
      <div
        class="nxp-textarea-scroll-thumb"
        :style="{ height: `${metrics.thumb}px`, transform: `translateY(${metrics.offset}px)` }"
        role="scrollbar"
        tabindex="0"
        aria-orientation="vertical"
        :aria-controls="inputId"
        aria-valuemin="0"
        aria-valuemax="100"
        :aria-valuenow="Math.round(metrics.value / Math.max(1, (input?.scrollHeight || 1) - (input?.clientHeight || 1)) * 100)"
        @pointerdown="onThumbPointerDown"
        @keydown="onScrollbarKeydown"
      />
    </div>
  </div>
</template>

<style>
.nxp-textarea-shell { position: relative; width: 100%; min-width: 0; }
.nxp-textarea-shell > .nxp-textarea { display: block; width: 100%; min-height: 96px; padding-block: var(--nx-space-3); resize: vertical; overscroll-behavior: contain; scrollbar-width: none; -ms-overflow-style: none; }
.nxp-textarea-shell > .nxp-textarea::-webkit-scrollbar { width: 0; height: 0; }
.nxp-textarea-scroll-track { position: absolute; z-index: 2; top: 4px; right: 2px; bottom: 8px; width: 8px; pointer-events: none; opacity: 0; transition: opacity var(--nx-motion-duration-fast, .15s) var(--nx-motion-ease, ease); }
.nxp-textarea-shell:hover .nxp-textarea-scroll-track,
.nxp-textarea-shell:focus-within .nxp-textarea-scroll-track,
.nxp-textarea-shell.is-scroll-visible .nxp-textarea-scroll-track { opacity: 1; }
.nxp-textarea-scroll-thumb { position: absolute; top: 0; right: 0; box-sizing: border-box; width: 100%; min-height: 8px; border: 0; border-radius: 999px; background: color-mix(in srgb, var(--nx-color-text, #7d8798) 48%, transparent); cursor: grab; pointer-events: auto; }
.nxp-textarea-scroll-thumb:hover, .nxp-textarea-scroll-thumb:focus-visible { background: color-mix(in srgb, var(--nx-color-text, #7d8798) 68%, transparent); outline: none; }
.nxp-textarea-scroll-thumb:active { cursor: grabbing; }
@media (prefers-reduced-motion: reduce) { .nxp-textarea-scroll-track { transition: none; } }
</style>
