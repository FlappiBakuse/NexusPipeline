<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { MediaPool } from '../control/media-pool';
import MediaImage from '../components/MediaImage.vue';
import { ModalSurface } from '../ui/modal-surface';
import { t, registerMessages } from '../app/locale';
import './history-image-preview.css';

registerMessages({
  放大: 'Zoom in',
  缩小: 'Zoom out',
  重置缩放: 'Reset zoom',
  保存原图: 'Save original image'
});
const props = defineProps<{ pool: MediaPool; path: string; visible: boolean; reason?: string }>();
const emit = defineEmits<{ close: []; download: [] }>();
const dialog = ref<HTMLDialogElement>();
const viewport = ref<HTMLElement>();
const anchor = ref<HTMLElement>();
const menu = ref(false);
const menuPoint = ref({ x: 0, y: 0 });
const natural = ref({ width: 0, height: 0 });
const size = ref({ width: 0, height: 0 });
const zoom = ref(1);
const pan = ref({ x: 0, y: 0 });
const fitted = computed(() => {
  const ratio = Math.min(size.value.width / natural.value.width, size.value.height / natural.value.height);
  return Number.isFinite(ratio)
    ? { width: natural.value.width * ratio, height: natural.value.height * ratio }
    : size.value;
});
const imageStyle = computed(() => ({
  width: fitted.value.width + 'px',
  height: fitted.value.height + 'px',
  left: pan.value.x + 'px',
  top: pan.value.y + 'px',
  transform: `scale(${zoom.value})`
}));
const pointers = new Map<number, { x: number; y: number }>();
let surface: ModalSurface | undefined;
let resize: ResizeObserver | undefined;
const lifetime = new AbortController();
function clamp(): void {
  const x = Math.max(0, (fitted.value.width * zoom.value - size.value.width) / 2);
  const y = Math.max(0, (fitted.value.height * zoom.value - size.value.height) / 2);
  pan.value = { x: Math.max(-x, Math.min(x, pan.value.x)), y: Math.max(-y, Math.min(y, pan.value.y)) };
}
function changeZoom(value: number, point = { x: 0, y: 0 }): void {
  const next = Math.max(0.5, Math.min(2, value));
  const ratio = next / zoom.value;
  pan.value = { x: point.x - (point.x - pan.value.x) * ratio, y: point.y - (point.y - pan.value.y) * ratio };
  zoom.value = next;
  clamp();
}
function wheel(event: WheelEvent): void {
  event.preventDefault();
  const rect = viewport.value!.getBoundingClientRect();
  changeZoom(zoom.value * Math.exp(-event.deltaY * 0.0015), {
    x: event.clientX - rect.left - rect.width / 2,
    y: event.clientY - rect.top - rect.height / 2
  });
}
function pointerDown(event: PointerEvent): void {
  if (event.button !== 0) return;
  menu.value = false;
  pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
  (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
}
function pointerMove(event: PointerEvent): void {
  const prior = pointers.get(event.pointerId);
  if (!prior) return;
  const other = [...pointers.entries()].find(([id]) => id !== event.pointerId)?.[1];
  if (other) {
    const before = Math.hypot(prior.x - other.x, prior.y - other.y);
    if (before > 0)
      changeZoom((zoom.value * Math.hypot(event.clientX - other.x, event.clientY - other.y)) / before);
  } else {
    pan.value = { x: pan.value.x + event.clientX - prior.x, y: pan.value.y + event.clientY - prior.y };
    clamp();
  }
  pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
}
function contextMenu(event: MouseEvent): void {
  event.preventDefault();
  menuPoint.value = { x: event.clientX, y: event.clientY };
  menu.value = true;
  void nextTick(() => dialog.value?.querySelector<HTMLElement>('nexus-menu')?.focus());
}
watch(viewport, (element) => {
  resize?.disconnect();
  if (!element) return;
  resize = new ResizeObserver(() => {
    size.value = { width: element.clientWidth, height: element.clientHeight };
    clamp();
  });
  resize.observe(element);
});
watch(
  [() => props.visible, () => props.path],
  ([visible]) => {
    menu.value = false;
    pointers.clear();
    zoom.value = 1;
    pan.value = { x: 0, y: 0 };
    natural.value = { width: 0, height: 0 };
    surface?.show(visible);
  }
);
onMounted(() => {
  surface = new ModalSurface(dialog.value!, () => {
    if (menu.value) menu.value = false;
    else emit('close');
  });
  surface.connect(lifetime.signal);
  surface.show(props.visible);
});
onBeforeUnmount(() => {
  lifetime.abort();
  resize?.disconnect();
  pointers.clear();
  surface?.close(true);
});
</script>

<template>
  <dialog ref="dialog" class="history-image-preview" :aria-label="t('截图原件')">
    <div class="mask"></div>
    <div class="panel">
      <div class="history-image-tools">
        <button :aria-label="t('缩小')" :disabled="zoom <= 0.5" @click="changeZoom(zoom - 0.1)">−</button>
        <button
          class="image-zoom-reset"
          :aria-label="t('重置缩放')"
          @click="
            zoom = 1;
            pan = { x: 0, y: 0 };
          "
        >
          {{ Math.round(zoom * 100) }}%
        </button>
        <button :aria-label="t('放大')" :disabled="zoom >= 2" @click="changeZoom(zoom + 0.1)">+</button>
      </div>
      <button class="close history-image-close" :aria-label="t('关闭')">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path
            d="m6 6 12 12M18 6 6 18"
            fill="none"
            stroke="currentColor"
            stroke-width="1.5"
            stroke-linecap="round"
          />
        </svg>
      </button>
      <div
        ref="viewport"
        class="image-viewport"
        role="group"
        tabindex="0"
        :aria-label="t('截图视口')"
        @wheel="wheel"
        @pointerdown="pointerDown"
        @pointermove="pointerMove"
        @pointerup="pointers.delete($event.pointerId)"
        @pointercancel="pointers.delete($event.pointerId)"
        @lostpointercapture="pointers.delete($event.pointerId)"
        @contextmenu="contextMenu"
      >
        <div class="history-image-position" :style="imageStyle">
          <MediaImage
            :pool="pool"
            :path="path"
            :visible="visible"
            :description="t('运行截图')"
            @loaded="
              natural = $event;
              clamp();
            "
          />
        </div>
      </div>
      <p class="history-image-caption">{{ natural.width }}×{{ natural.height }}<template v-if="reason"> · {{ reason }}</template></p>
      <span
        ref="anchor"
        class="history-image-menu-anchor"
        :style="{ left: menuPoint.x + 'px', top: menuPoint.y + 'px' }"
      ></span>
      <nexus-menu
        :open.prop="menu"
        :anchor.prop="anchor || null"
        :items.prop="[{ id: 'download', label: t('保存原图') }]"
        @nexus-close="menu = false"
        @nexus-action="
          menu = false;
          emit('download');
        "
      />
    </div>
  </dialog>
</template>
