<script setup lang="ts">
import type { DashboardCard } from '../control/interface';
import { t } from '../app/locale';
const props = defineProps<{
  cards: DashboardCard[];
  catalog: { id: DashboardCard; title: string; description: string }[];
  locked: boolean;
}>();
const emit = defineEmits<{ change: [cards: DashboardCard[]] }>();
</script>

<template>
  <div class="layout-fields">
    <p>
      {{ t('拖动把手或使用把手上的键盘方向键排序。修改立即生效，取消可恢复本次打开前的布局。') }}
    </p>
    <h3>{{ t('当前显示') }}</h3>
    <nexus-sortable-list
      class="layout-list"
      :ids.prop="cards"
      :disabled.prop="locked"
      @nexus-reorder="emit('change', $event.detail.ids)"
    >
      <article v-for="id in cards" :key="id" :data-item-id="id">
        <nexus-drag-handle :item-id="id" :label="t('调整卡片顺序')" :disabled.prop="locked" />
        <span>
          <strong>{{ t(catalog.find((item) => item.id === id)?.title || id) }}</strong>
          <small>{{ t(catalog.find((item) => item.id === id)?.description || '') }}</small>
        </span>
        <button
          :disabled="locked"
          @click="
            emit(
              'change',
              cards.filter((item) => item !== id)
            )
          "
        >
          {{ t('移除') }}
        </button>
      </article>
    </nexus-sortable-list>
    <h3>{{ t('可添加') }}</h3>
    <div class="layout-list">
      <p v-if="cards.length === catalog.length" class="all-added">{{ t('所有可用卡片均已加入') }}</p>
      <article v-for="item in catalog.filter((item) => !cards.includes(item.id))" :key="item.id">
        <span>
          <strong>{{ t(item.title) }}</strong>
          <small>{{ t(item.description) }}</small>
        </span>
        <button :disabled="locked" @click="emit('change', [...cards, item.id])">{{ t('添加') }}</button>
      </article>
    </div>
  </div>
</template>

<style scoped>
.layout-fields {
  display: grid;
}
.layout-fields > p {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.6;
  margin: 12px 0 28px;
}
h3 {
  font-size: 15px;
  line-height: 1.35;
  margin: 0 0 12px;
}
.layout-list + h3 {
  margin-top: 16px;
}
.layout-list {
  display: block;
  border: 1px solid var(--border);
  border-radius: 16px;
  overflow: hidden;
  background: var(--nx-color-surface);
}
article {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px 8px;
}
article + article {
  border-top: 1px solid var(--border);
}
article > span {
  display: grid;
  gap: 4px;
  flex: 1;
}
small,
.all-added {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.6;
}
.all-added {
  padding: 16px;
  margin: 0;
}
</style>
