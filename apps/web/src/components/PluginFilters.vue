<script setup lang="ts">
import { onBeforeUnmount, ref, watch, nextTick } from 'vue';
import { t } from '../app/locale';
const props = defineProps<{ kind: string; order: string; descending: boolean; visible: boolean }>();
const emit = defineEmits<{ kind: [value: string]; order: [value: string]; descending: [value: boolean] }>();
const open = ref(false),
  root = ref<HTMLElement>(),
  trigger = ref<HTMLButtonElement>(),
  panel = ref<HTMLElement>();
const controller = new AbortController();
function close(): void {
  open.value = false;
  trigger.value?.focus({ preventScroll: true });
}
document.addEventListener(
  'pointerdown',
  (event) => {
    if (open.value && !root.value?.contains(event.target as Node)) open.value = false;
  },
  { signal: controller.signal }
);
document.addEventListener(
  'keydown',
  (event) => {
    if (!open.value) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      close();
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const buttons = Array.from(panel.value?.querySelectorAll('button') || []);
      const index = buttons.indexOf(document.activeElement as HTMLButtonElement),
        delta = event.key === 'ArrowDown' ? 1 : -1;
      buttons[(index + delta + buttons.length) % buttons.length]?.focus();
      event.preventDefault();
    }
  },
  { signal: controller.signal }
);
watch(open, async (active) => {
  if (active) {
    await nextTick();
    panel.value?.querySelector('button')?.focus();
  }
});
watch(
  () => props.visible,
  (visible) => {
    if (!visible) open.value = false;
  }
);
onBeforeUnmount(() => controller.abort());
</script>
<template>
  <div ref="root" class="plugin-filter">
    <button ref="trigger" :aria-expanded="open" aria-haspopup="dialog" @click="open = !open">
      <nexus-icon name="filter" aria-hidden="true" />
      {{ t('筛选') }}
    </button>
    <div v-if="open" ref="panel" class="plugin-filter-panel" role="dialog" :aria-label="t('插件筛选与排序')">
      <p>{{ t('插件类型') }}</p>
      <button
        v-for="item in [
          ['all', '全部'],
          ['managed-code', '通用插件'],
          ['data-specialized', '专项插件']
        ]"
        :key="item[0]"
        :class="{ active: kind === item[0] }"
        :aria-pressed="kind === item[0]"
        @click="emit('kind', item[0])"
      >
        {{ t(item[1]) }}
        <nexus-icon v-if="kind === item[0]" name="check" aria-hidden="true" />
      </button>
      <p class="separator">{{ t('排序') }}</p>
      <button
        v-for="item in [
          ['name', '名称（首字母 / 拼音）'],
          ['created', '创建时间'],
          ['updated', '更新时间']
        ]"
        :key="item[0]"
        :class="{ active: order === item[0] }"
        :aria-pressed="order === item[0]"
        @click="emit('order', item[0])"
      >
        {{ t(item[1]) }}
        <nexus-icon v-if="order === item[0]" name="check" aria-hidden="true" />
      </button>
      <div class="directions">
        <button
          :class="{ active: !descending }"
          :aria-pressed="!descending"
          @click="emit('descending', false)"
        >
          {{ t('正序') }}
          <nexus-icon v-if="!descending" name="check" aria-hidden="true" />
        </button>
        <button :class="{ active: descending }" :aria-pressed="descending" @click="emit('descending', true)">
          {{ t('逆序') }}
          <nexus-icon v-if="descending" name="check" aria-hidden="true" />
        </button>
      </div>
    </div>
  </div>
</template>
<style scoped>
.plugin-filter {
  position: relative;
  flex: none;
}
.plugin-filter > button {
  display: flex;
  align-items: center;
  gap: 7px;
  width: 76px;
  padding: 0 12px;
}
nexus-icon {
  width: 16px;
  height: 16px;
  flex: none;
}
.plugin-filter-panel {
  position: absolute;
  top: calc(100% + 6px);
  right: 0;
  z-index: 40;
  width: 248px;
  padding: 8px 12px 12px;
  background: var(--panel-opaque);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: var(--shadow);
}
.plugin-filter-panel p {
  margin: 2px 8px 4px;
  color: var(--faint);
  font-size: 11px;
  font-weight: 400;
}
.plugin-filter-panel .separator {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 10px;
  margin-bottom: 12px;
}
.separator::after {
  content: '';
  height: 1px;
  flex: 1;
  background: var(--border);
}
.plugin-filter-panel button {
  width: 100%;
  display: flex;
  justify-content: space-between;
  align-items: center;
  min-height: 40px;
  padding: 0 8px;
  border-color: transparent;
  background: transparent;
  font-size: 12px;
  text-align: left;
}
.plugin-filter-panel button + button {
  margin-top: 2px;
}
.plugin-filter-panel button.active {
  color: var(--accent);
  border-color: var(--border);
  background: var(--content-card-soft);
}
.directions {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 4px;
  margin-top: 2px;
}
.directions > button + button {
  margin: 0;
}
</style>
