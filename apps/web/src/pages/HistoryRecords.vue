<script setup lang="ts">
import { computed } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import type { RecordPage, RecordRow } from '../control/models';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import { historyRow, recordBusiness, recordLifecycle } from './history-row';
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
      <p v-if="error" role="alert">
        {{ t(error) }}
        <button @click="emit('retry')">{{ t('重试') }}</button>
      </p>
      <p v-if="loading && !page?.items.length" role="status">{{ t('加载中') }}</p>
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
                :title="recordBusiness(row) + ' · ' + recordLifecycle(row)"
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
    <p v-if="linkError" role="alert">{{ t(linkError) }}</p>
  </nexus-scroll-area>
</template>
<style scoped src="./history-records.css"></style>
