<script setup lang="ts" generic="T">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';

const props = defineProps<{
  items: readonly T[];
  itemKey: (item: T) => string;
  label: string;
  visible: boolean;
}>();
const root = ref<HTMLElement>();
const top = ref(0);
const height = ref(264);
const stride = 44;
const first = computed(() => Math.max(0, Math.floor(top.value / stride) - 2));
const end = computed(() => Math.min(props.items.length, first.value + Math.ceil(height.value / stride) + 5));
const rows = computed(() => props.visible
  ? props.items.slice(first.value, end.value).map((item, offset) => ({ item, index: first.value + offset })) : []);
let resize: ResizeObserver | undefined;
watch(root, element => {
  resize?.disconnect();
  if (!element) return;
  resize = new ResizeObserver(() => { if (element.clientHeight) height.value = element.clientHeight; });
  resize.observe(element);
});
watch(() => props.items[0] === undefined ? '' : props.itemKey(props.items[0]), () => {
  top.value = 0;
  if (root.value) root.value.scrollTop = 0;
});
watch(() => props.items.length, length => {
  if (top.value < length * stride) return;
  top.value = Math.max(0, length * stride - height.value);
  if (root.value) root.value.scrollTop = top.value;
});
async function navigate(event: KeyboardEvent): Promise<void> {
  if (!root.value || !(event.target instanceof Element)) return;
  const row = event.target.closest<HTMLElement>('[data-catalog-index]');
  if (!row?.querySelector('button')) return;
  const index = Number(row.dataset.catalogIndex);
  const target = event.key === 'Home' ? 0 : event.key === 'End' ? props.items.length - 1
    : event.key === 'ArrowDown' ? index + 1 : event.key === 'ArrowUp' ? index - 1 : null;
  if (target === null) return;
  event.preventDefault();
  const bounded = Math.max(0, Math.min(props.items.length - 1, target));
  root.value.scrollTop = bounded * stride;
  top.value = root.value.scrollTop;
  await nextTick();
  root.value.querySelector<HTMLButtonElement>(`[data-catalog-index="${bounded}"] button`)?.focus();
}
onBeforeUnmount(() => resize?.disconnect());
</script>

<template>
  <nexus-scroll-area ref="root" class="history-catalog-window" direction="vertical" :label="label"
    :aria-label="label" tabindex="0" :style="{ height: Math.min(items.length, 6) * stride + 'px' }"
    @scroll="top = ($event.target as HTMLElement).scrollTop" @keydown="navigate">
    <div role="list" class="history-catalog-space" :style="{ height: items.length * stride + 'px' }">
      <div v-for="row in rows" :key="itemKey(row.item)" role="listitem" class="history-catalog-row"
        :aria-posinset="row.index + 1" :aria-setsize="items.length" :data-catalog-index="row.index"
        :style="{ top: row.index * stride + 'px' }">
        <slot :item="row.item" :index="row.index" />
      </div>
    </div>
  </nexus-scroll-area>
</template>

<style scoped>
.history-catalog-window { width: 100%; overflow-anchor: none; }
.history-catalog-space { position: relative; }
.history-catalog-row { position: absolute; left: 0; right: 0; height: 44px; display: flex; align-items: center; }
.history-catalog-row :deep(button), .history-catalog-row :deep(p) {
  width: 100%; min-width: 0; margin: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis;
}
.history-catalog-row :deep(button) { text-align: start; }
</style>
