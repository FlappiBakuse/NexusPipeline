<script setup lang="ts">
import { fieldHelp as vHelp } from '../components/field-help';
import { computed } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import type { RecordPage, RecordRow } from '../control/models';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import { historyRow, recordBusiness, recordLifecycle } from './history-format';
import { t } from '../app/locale';
const props = defineProps<{
  store: RecordsStore;
  visible: boolean;
  page?: RecordPage;
  pageNumber: number;
  total: number | null;
  userSelected: boolean;
  selectedName: string;
  loading: boolean;
  error: string;
  linkError: string;
}>();
const emit = defineEmits<{ back: []; retry: []; select: [row: RecordRow]; page: [number: number] }>();
const recordRows = computed(() =>
  (props.page?.items || []).map((record) => ({ record, view: historyRow(record) }))
);
</script>
<template>
  <nexus-scroll-area class="history-records" :label="t('运行历史')">
    <nexus-empty-state
      v-if="!userSelected"
      :title="t('选择运行用户')"
      :description="t('点击日期下的用户查看当天运行记录。')"
    />
    <template v-else>
      <button class="back-history" @click="emit('back')">‹ {{ t('返回用户列表') }}</button>
      <header>
        <strong>{{ t(selectedName) }}</strong>
        <nexus-badge :label="(page?.items.length || 0) + ' ' + t('条记录')" />
      </header>
      <nexus-toast v-if="error" tone="danger" :message.prop="t(error)" />
    <button v-if="error" type="button" @click="emit('retry')">{{ t('重试') }}</button>
      <nexus-spinner v-if="loading && !page?.items.length" :label="t('加载中')" />
      <nexus-empty-state v-else-if="!page?.items.length" :title="t('暂无历史记录')" />
      <article v-for="{ record: row, view } in recordRows" :key="row.runId" class="record-card">
        <button class="record-open" @click="emit('select', row)">
          <span class="record-main">
            <span class="record-title">
              <strong>
                {{
                  store.state.automation?.bundle.scripts.find((item) => item.id === row.automationId)?.name ||
                  store.state.automation?.bundle.queues.find((item) => item.id === row.automationId)?.name ||
                  t('脚本组')
                }}
              </strong>
              <nexus-badge
                :label="view.label"
                :tone="view.tone"
                v-help="recordBusiness(row) + ' · ' + recordLifecycle(row)"
              />
            </span>
            <small>{{ view.description }}</small>
          </span>
          <nexus-icon aria-hidden="true" name="chevronRight" />
        </button>
        <ExtensionOutlet
          :store="store"
          target="records.list.badges"
          :entity-id="row.runId"
          :run-id="row.runId"
          :visible="visible"
        />
      </article>
      <nexus-pager
        :page.prop="pageNumber"
        :page-size.prop.camel="50"
        :total.prop="total"
        :has-next.prop.camel="!!page?.next"
        :inert="loading"
        @nexus-change="emit('page', $event.detail.value)"
      />
    </template>
    <nexus-toast v-if="linkError" tone="danger" :message.prop="t(linkError)" />
  </nexus-scroll-area>
</template>
<style scoped>
.history-records {
  min-height: 0;
  min-width: 0;
  overflow: hidden;
  padding: 8px;
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--panel);
}
.history-records > header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  min-height: 48px;
  padding: 0 14px;
}
.history-records > nexus-empty-state {
  border: 0;
  background: transparent;
}
.back-history {
  display: none;
}
.record-card {
  border: 1px solid var(--border);
  border-radius: 12px;
  margin-bottom: 8px;
  background: var(--content-card-soft);
}
.record-open {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 14px;
  border: 0;
  background: transparent;
  text-align: left;
}
.record-main {
  display: grid;
  gap: 4px;
  min-width: 0;
  flex: 1;
}
.record-title {
  display: flex;
  align-items: center;
  gap: 8px;
}
.record-main strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.record-main small {
  color: var(--muted);
  font-size: 12px;
  line-height: 18px;
}
.record-open > nexus-icon {
  color: var(--accent);
  flex: none;
  width: 16px;
}
@media (max-width: 767px) {
  .back-history {
    display: block;
    width: 100%;
    text-align: left;
    margin-bottom: 8px;
  }
  .record-open {
    flex-wrap: wrap;
  }
}
</style>
