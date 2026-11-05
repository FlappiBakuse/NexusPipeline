<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import type { MediaPool } from '../control/media-pool';
import MediaImage from './MediaImage.vue';
import { registerMessages, t } from '../app/locale';

registerMessages({ 截图列表: 'Screenshots', 运行截图: 'Run screenshots', 张: 'images' });
const props = defineProps<{ pool: MediaPool; paths: readonly string[]; visible: boolean }>();
const emit = defineEmits<{ select: [index: number] }>();
const root = ref<HTMLElement>();
const width = ref(240);
const left = ref(0);
const stride = 122;
const first = computed(() => Math.max(0, Math.floor(left.value / stride) - 2));
const end = computed(() => Math.min(props.paths.length, first.value + Math.ceil(width.value / stride) + 5));
const items = computed(() =>
  props.visible
    ? props.paths.slice(first.value, end.value).map((path, offset) => ({ path, index: first.value + offset }))
    : []
);
let resize: ResizeObserver | undefined;
watch(root, (element) => {
  resize?.disconnect();
  if (!element) return;
  resize = new ResizeObserver(() => {
    if (element.clientWidth) width.value = element.clientWidth;
    left.value = element.scrollLeft;
  });
  resize.observe(element);
});
watch(
  () => props.paths,
  (paths, previous) => {
    if (paths[0] !== previous?.[0] && root.value) {
      root.value.scrollLeft = 0;
      left.value = 0;
    }
  }
);
async function focusItem(event: KeyboardEvent, index: number): Promise<void> {
  const target =
    event.key === 'Home'
      ? 0
      : event.key === 'End'
        ? props.paths.length - 1
        : event.key === 'ArrowRight'
          ? index + 1
          : event.key === 'ArrowLeft'
            ? index - 1
            : null;
  if (target === null || !root.value) return;
  event.preventDefault();
  const bounded = Math.max(0, Math.min(props.paths.length - 1, target));
  root.value.scrollLeft = bounded * stride;
  left.value = root.value.scrollLeft;
  await nextTick();
  root.value.querySelector<HTMLButtonElement>(`button[data-image-index="${bounded}"]`)?.focus();
}
onBeforeUnmount(() => resize?.disconnect());
</script>

<template>
  <section v-if="paths.length" class="history-screenshots">
    <p role="status">{{ t('运行截图') }}（{{ paths.length }} {{ t('张') }}）</p>
    <nexus-scroll-area
      ref="root"
      class="image-gallery"
      direction="horizontal"
      role="list"
      :label="t('截图列表')"
      :aria-label="t('截图列表')"
      @scroll="left = ($event.target as HTMLElement).scrollLeft"
    >
      <div
        class="image-gallery-space"
        role="presentation"
        :style="{ width: Math.max(0, paths.length * stride - 10) + 'px' }"
      >
        <div
          v-for="item in items"
          :key="item.path"
          class="image-gallery-item"
          role="listitem"
          :aria-label="`${item.index + 1} / ${paths.length}`"
          :aria-posinset="item.index + 1"
          :aria-setsize="paths.length"
          :style="{ left: item.index * stride + 'px' }"
        >
          <button
            class="image-button"
            :data-image-index="item.index"
            :aria-label="`${t('运行截图')} ${item.index + 1}`"
            @click="emit('select', item.index)"
            @keydown="focusItem($event, item.index)"
          >
            <MediaImage
              :pool="pool"
              :path="item.path"
              :visible="visible"
              :description="t('运行截图')"
              preview
            />
            <span class="image-number">{{ item.index + 1 }}</span>
          </button>
        </div>
      </div>
    </nexus-scroll-area>
  </section>
</template>

<style scoped>
.history-screenshots {
  margin-top: 20px;
}
.history-screenshots > p {
  margin: 0 0 10px;
  color: var(--muted);
  font-size: 12px;
}
.image-gallery {
  height: 88px;
  overflow: hidden;
  overflow-anchor: none;
}
.image-gallery-space {
  position: relative;
  height: 76px;
}
.image-gallery-item {
  position: absolute;
  top: 0;
  width: 112px;
  height: 76px;
}
.image-button {
  position: relative;
  display: block;
  width: 112px;
  height: 76px;
  min-height: 0;
  padding: 0;
  overflow: hidden;
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  background: transparent;
}
.image-button :deep(.media-image) {
  width: 100%;
  height: 100%;
  min-height: 0;
  margin: 0;
}
.image-button :deep(canvas) {
  width: 100%;
  height: 100%;
  object-fit: contain;
}
.image-number {
  position: absolute;
  right: 5px;
  bottom: 5px;
  padding: 1px 6px;
  border-radius: 20px;
  background: #071321dd;
  color: #fff;
  font-size: 10px;
  line-height: 16px;
}
</style>
