<script setup lang="ts">
import { computed, ref, watch, onBeforeUnmount } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import { recordPage } from '../control/models';
import type { RecordPage, RecordRow } from '../control/models';
import { objectValue, arrayValue } from '../control/client';
import HistoryDetail from './HistoryDetail.vue';
import ModalTransition from '../components/ModalTransition.vue';
import { t } from '../app/locale';
import './history-messages';
import type { DateRange } from '@nexuspipeline/extension-sdk';
import { RecordWindow } from './record-window';
import HistoryDescription from './HistoryDescription.vue';
import HistoryIndex from './HistoryIndex.vue';
import { outcomeName } from './history-row';
import HistoryRecords from './HistoryRecords.vue';
import { HistoryLink } from './history-link';
import { usePanelHeight } from '../app/panel-height';
import { localDate, timeRange } from './history-range';
const props = defineProps<{ store: RecordsStore; visible: boolean }>();
const workspace = ref<HTMLElement>();
const { height: workspaceHeight } = usePanelHeight(workspace, () => props.visible);
const page = ref<RecordPage>();
const pageNumber = ref(1);
const records = new RecordWindow();
const user = ref('');
const business = ref('');
const lifecycle = ref('');
const selectedStatus = computed({
  get: () => lifecycle.value === 'cancelled' ? 'cancelled' : business.value,
  set: (value: string) => {
    lifecycle.value = value === 'cancelled' ? 'cancelled' : '';
    business.value = value === 'cancelled' ? '' : value;
  }
});
const from = ref('');
const until = ref('');
const loading = ref(false);
const summary = ref<Record<string, unknown>>();
const chosenDate = ref('');
const chosenUser = ref('');
const selectedTotal = ref<number | null>(null);
const summaryBusy = ref(false);
const summaryError = ref('');
const recordError = ref('');
const today = new Date();
until.value = localDate(today);
from.value = localDate(new Date(today.getFullYear(), today.getMonth(), today.getDate() - 29));
const dateRange = computed<DateRange>(() => ({ from: from.value || null, to: until.value || null }));
function applyRange(event: CustomEvent<{ value: DateRange }>): void {
  const { from: start, to: end } = event.detail.value;
  if (!start || !end) return;
  from.value = start;
  until.value = end;
}
let owner: AbortController | undefined;
let summaryOwner: AbortController | undefined;
const users = computed(() => props.store.state.automation?.bundle.users || []);
const days = computed(() => arrayValue(summary.value?.days || [], objectValue));
const selectedName = computed(() =>
  chosenUser.value === 'null'
    ? '脚本组'
    : users.value.find((item) => item.id === (chosenUser.value || user.value))?.name ||
      chosenUser.value ||
      user.value ||
      '全部账号'
);
function summaryQuery(start: string, end: string): string {
  const range = timeRange(start, end);
  const query = new URLSearchParams({
    endedFromInclusive: range.from,
    endedUntilExclusive: range.until,
    offsetMinutes: String(-new Date().getTimezoneOffset())
  });
  if (business.value) query.set('business', business.value);
  if (lifecycle.value) query.set('lifecycle', lifecycle.value);
  if (user.value) query.set('userId', user.value);
  return '/control/v1/records/summary/days?' + query;
}
async function loadSummary(): Promise<void> {
  summaryOwner?.abort();
  selectedTotal.value = null;
  if (!props.visible || !from.value || !until.value) return;
  const controller = new AbortController();
  summaryOwner = controller;
  summaryBusy.value = true;
  summaryError.value = '';
  try {
    if (from.value > until.value || (Date.parse(until.value) - Date.parse(from.value)) / 86400000 > 365)
      throw Error('日期范围须在 366 天内');
    const result = await props.store.client.read(
      summaryQuery(from.value, until.value),
      objectValue,
      controller.signal
    );
    controller.signal.throwIfAborted();
    summary.value = result;
    if (
      !selected.value &&
      (!chosenDate.value ||
        !arrayValue(result.days, objectValue).some((item) => item.date === chosenDate.value))
    )
      chosenDate.value = String(arrayValue(result.days, objectValue)[0]?.date || '');
  } catch (error) {
    if (!controller.signal.aborted)
      summaryError.value = error instanceof Error ? error.message : String(error);
  } finally {
    if (summaryOwner === controller) summaryBusy.value = false;
  }
}
async function load(target = 1, reset = true): Promise<void> {
  owner?.abort();
  if (!props.visible || !chosenUser.value) {
    page.value = undefined;
    return;
  }
  const controller = new AbortController();
  owner = controller;
  loading.value = true;
  recordError.value = '';
  const query = new URLSearchParams({ limit: '50' });
  if (chosenUser.value === 'null') query.set('scope', 'groups');
  if (chosenUser.value && chosenUser.value !== 'null') query.set('userId', chosenUser.value);
  else if (user.value && chosenUser.value !== 'null') query.set('userId', user.value);
  if (business.value) query.set('business', business.value);
  if (lifecycle.value) query.set('lifecycle', lifecycle.value);
  const range = timeRange(chosenDate.value || from.value, chosenDate.value || until.value);
  query.set('endedFromInclusive', range.from);
  query.set('endedUntilExclusive', range.until);
  try {
    const fetchPage = (cursor: RecordPage['next'], limit: number) => {
      const requested = new URLSearchParams(query);
      if (cursor) {
        requested.set('afterEndedAt', cursor.endedAt);
        requested.set('afterRunId', cursor.runId);
      }
      requested.set('limit', String(limit));
      return props.store.client.read('/control/v1/records?' + requested, recordPage, controller.signal);
    };
    const result = await records.read(
      target,
      (cursor) => fetchPage(cursor, 50),
      controller.signal,
      reset
    );
    controller.signal.throwIfAborted();
    page.value = result.data;
    pageNumber.value = result.number;
  } catch (error) {
    if (!controller.signal.aborted) {
      recordError.value = error instanceof Error ? error.message : String(error);
      props.store.report(error);
    }
  } finally {
    if (owner === controller) loading.value = false;
  }
}
function updateTotal(date: string, id: string, total: number | null): void {
  if (props.visible && date === chosenDate.value && id === chosenUser.value) selectedTotal.value = total;
}
function chooseUser(date: string, id: string, total: number | null): void {
  chosenDate.value = date;
  chosenUser.value = id;
  updateTotal(date, id, total);
  clearSelection();
  void load();
}
function backToDates(): void {
  chosenUser.value = '';
  selectedTotal.value = null;
  page.value = undefined;
  clearSelection();
}
const link = new HistoryLink(
  props.store,
  () => props.visible,
  async (row) => {
    chosenDate.value = localDate(new Date(String(row.endedAt)));
    chosenUser.value = String(row.userId || 'null');
    selectedTotal.value = null;
    await load();
  },
  () => summaryOwner?.abort()
);
const { selected, detailOpen, attemptId, deepLinkError } = link;
const select = (row: RecordRow) => link.select(row);
const clearSelection = () => link.clear();
const chooseAttempt = (id: string) => link.chooseAttempt(id);
async function refreshPage(): Promise<void> {
  await loadSummary();
  await load();
}
watch(() => props.store.state.preparationRevision, () => {
  if (props.visible) void refreshPage();
});
watch([user, business, lifecycle, from, until], () => {
  chosenDate.value = '';
  chosenUser.value = '';
  selectedTotal.value = null;
  clearSelection();
  void refreshPage();
});
watch(
  () => props.visible,
  (visible) => {
    if (visible)
      void (async () => {
        await loadSummary();
        await load(pageNumber.value, false);
        await link.restore();
      })();
    else {
      owner?.abort();
      link.abort();
      summaryOwner?.abort();
    }
  },
  { immediate: true }
);
onBeforeUnmount(() => {
  owner?.abort();
  link.abort();
  summaryOwner?.abort();
  link.dispose();
});
</script>
<template>
  <HistoryDescription :from="from" :until="until" :visible="visible" />
  <div
    ref="workspace"
    class="history-layout"
    :data-selected="!!chosenUser"
    :style="{ height: workspaceHeight }"
  >
    <div class="history-left">
      <div class="history-filters">
        <nexus-date-range-picker
          :aria-label="t('日期范围')"
          :value.prop="dateRange"
          :max.prop="localDate(new Date())"
          @nexus-change="applyRange"
        />
        <FormSelect v-model="selectedStatus" :aria-label="t('全部状态')">
          <option value="">{{ t('全部状态') }}</option>
          <option
            v-for="item in ['succeeded', 'failed', 'partial', 'cancelled', 'notApplicable']"
            :key="item"
            :value="item"
          >
            {{ outcomeName(item) }}
          </option>
        </FormSelect>
      </div>
      <HistoryIndex
        :store="store"
        :days="days"
        :visible="visible"
        :selected-date="chosenDate"
        :selected-user="chosenUser"
        :business="business"
        :lifecycle="lifecycle"
        :loading="summaryBusy"
        :error="summaryError"
        @select="chooseUser"
        @count="updateTotal"
        @retry="loadSummary"
      />
    </div>
    <HistoryRecords
      :store="store"
      :visible="visible"
      :page="page"
      :page-number="pageNumber"
      :total="selectedTotal"
      :user-selected="!!chosenUser"
      :selected-name="selectedName"
      :loading="loading"
      :error="recordError"
      :link-error="deepLinkError"
      @back="backToDates"
      @retry="load(pageNumber, false)"
      @select="select"
      @page="load($event, false)"
    />
  </div>
  <ModalTransition>
    <HistoryDetail
      v-if="selected && detailOpen"
      :store="store"
      :row="selected"
      :attempt-id="attemptId"
      :visible="visible"
      @attempt="chooseAttempt"
      @close="detailOpen = false"
    />
  </ModalTransition>
</template>
<style scoped src="./history-page.css"></style>
