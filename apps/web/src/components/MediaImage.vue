<script setup lang="ts">
import { ref, watch, onBeforeUnmount } from 'vue';
import type { MediaPool, ImageLease } from '../control/media-pool';
import { t } from '../app/locale';
import { drawImage } from '../control/image-decode';
const props = defineProps<{
  pool: MediaPool;
  path: string;
  visible: boolean;
  description?: string;
  preview?: boolean;
}>();
const emit = defineEmits<{ loaded: [dimensions: { width: number; height: number }] }>();
const root = ref<HTMLElement>();
const canvas = ref<HTMLCanvasElement>();
const shown = ref(false);
const drawn = ref(false);
const error = ref('');
const previewWidth = ref(0);
let observer: IntersectionObserver | undefined;
let resize: ResizeObserver | undefined;
let owner: AbortController | undefined;
let lease: ImageLease | undefined;
function clear(): void {
  drawn.value = false;
  if (canvas.value) canvas.value.width = canvas.value.height = 0;
}
watch(root, (element) => {
  observer?.disconnect();
  resize?.disconnect();
  if (!element) return;
  observer = new IntersectionObserver((entries) => {
    shown.value = entries[0]?.isIntersecting === true;
  });
  observer.observe(element);
  resize = new ResizeObserver(() => {
    if (element.clientWidth) previewWidth.value = Math.ceil(element.clientWidth * devicePixelRatio / 64) * 64;
  });
  resize.observe(element);
});
watch(
  [() => props.path, () => props.visible, shown, () => (props.preview ? previewWidth.value : 0)],
  async () => {
    owner?.abort();
    lease?.release();
    lease = undefined;
    clear();
    error.value = '';
    if (!props.visible || !shown.value || (props.preview && !previewWidth.value)) return;
    const controller = new AbortController();
    owner = controller;
    try {
      const next = await props.pool.acquire(props.path, controller.signal);
      if (controller.signal.aborted) {
        next.release();
        return;
      }
      lease = next;
      const targetWidth = props.preview ? Math.min(next.width, previewWidth.value) : next.width;
      const targetHeight = Math.max(1, Math.round((next.height * targetWidth) / next.width));
      if (!canvas.value) return;
      await drawImage(next.blob, canvas.value, targetWidth, targetHeight, controller.signal);
      if (controller.signal.aborted || owner !== controller) return;
      drawn.value = true;
      emit('loaded', { width: next.width, height: next.height });
    } catch (failure) {
      if (!controller.signal.aborted && owner === controller) {
        lease?.release();
        lease = undefined;
        clear();
        error.value = failure instanceof Error ? failure.message : 'image_unavailable';
      }
    } finally {
      if (owner === controller) {
        lease?.release();
        lease = undefined;
      }
    }
  },
  { immediate: true }
);
onBeforeUnmount(() => {
  observer?.disconnect();
  resize?.disconnect();
  owner?.abort();
  lease?.release();
  clear();
});
</script>
<template>
  <figure ref="root" class="media-image">
    <canvas
      ref="canvas"
      v-show="drawn"
      width="0"
      height="0"
      role="img"
      :aria-hidden="!drawn"
      :aria-label="description || t('运行截图')"
    />
    <figcaption v-if="!drawn">{{ t(error || '等待图片进入可见区域') }}</figcaption>
    <slot />
  </figure>
</template>
