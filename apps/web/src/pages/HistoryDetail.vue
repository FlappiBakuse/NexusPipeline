<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import type { RecordRow } from '../control/models';
import { historyAttempts, historyDisplay, type HistoryAttempt, type HistoryDisplay } from '../control/records';
import { executionRun, type ActiveStep, type ActiveRun } from '../control/execution';
import { historyRow } from './history-row';
import {
  historyDuration,
  historyTime,
  historyReason,
  startedInvocations
} from './history-detail';
import AttemptDetail from './AttemptDetail.vue';
import HistoryTasks from './HistoryTasks.vue';
import { t } from '../app/locale';
import './history-detail.css';

const props = defineProps<{ store: RecordsStore; row: RecordRow; attemptId: string; visible: boolean }>();
const emit = defineEmits<{ close: []; attempt: [id: string] }>();
const attempts = ref<HistoryAttempt[]>([]);
const display = ref<HistoryDisplay>();
const steps = ref<ActiveStep[]>([]);
const report = ref<ActiveRun>();
const error = ref('');
const active = ref(true);
let owner: AbortController | undefined;
const name = computed(() =>
  String(
    display.value?.targetName ||
      props.store.state.automation?.bundle.scripts.find((item) => item.id === props.row.automationId)?.name ||
      props.store.state.automation?.bundle.queues.find((item) => item.id === props.row.automationId)?.name ||
      ''
  )
);
const userName = computed(() =>
  String(
    display.value?.accounts.find(account => account.userId === props.row.userId)?.name || display.value?.userName ||
      display.value?.accounts.map(account => account.name).join(' / ') ||
      props.store.state.automation?.bundle.users.find((item) => item.id === props.row.userId)?.name ||
      t('未知')
  )
);
const summary = computed(() => historyRow(props.row));
const finalFacts = computed(() => props.row.finalAttempt);
function timing(id: string) {
  return display.value?.attempts.find((item) => item.attemptId === id);
}
async function load(): Promise<void> {
  owner?.abort();
  if (!props.visible || !active.value) return;
  const controller = new AbortController();
  owner = controller;
  error.value = '';
  try {
    const base = '/control/v1/records/' + props.row.runId;
    const result = await props.store.client.read(
      base + '/attempts?limit=100&afterOrdinal=0',
      value => historyAttempts(value, props.row.runId),
      controller.signal
    );
    controller.signal.throwIfAborted();
    attempts.value = result;
    const details = await Promise.allSettled([
      props.store.client.read(base + '/display', value => historyDisplay(value, props.row.runId), controller.signal),
      props.store.client.read(
        base + '/attempts/' + finalFacts.value.attemptId + '/report',
        executionRun,
        controller.signal
      )
    ]);
    controller.signal.throwIfAborted();
    if (details[0].status === 'fulfilled') display.value = details[0].value;
    if (details[1].status === 'fulfilled') {
      report.value = details[1].value;
      steps.value = details[1].value.steps.filter(step => !props.row.userId || step.userId === props.row.userId);
    }
    for (const result of details) if (result.status === 'rejected') props.store.report(result.reason);
    if (props.attemptId) {
      await nextTick();
      document.getElementById('history-attempt-' + props.attemptId)?.scrollIntoView({ block: 'nearest' });
    }
  } catch (reason) {
    if (!controller.signal.aborted) error.value = reason instanceof Error ? reason.message : String(reason);
  }
}
function close(): void {
  active.value = false;
  owner?.abort();
  emit('close');
}
watch(
  () => [props.visible, props.row.runId, props.row.userId] as const,
  ([visible], before) => {
    owner?.abort();
    active.value = visible;
    if (before?.[1] !== props.row.runId || before?.[2] !== props.row.userId) {
      attempts.value = [];
      display.value = undefined;
      steps.value = [];
      report.value = undefined;
    }
    if (visible) void load();
  },
  { immediate: true }
);
onBeforeUnmount(() => owner?.abort());
</script>

<template>
  <nexus-modal
    :open.prop="visible && active"
    :title="(name ? name + ' ' : '') + t('运行详情')"
    size="wide"
    @nexus-close="close"
  >
    <div class="history-detail-content">
      <ul v-if="display?.diagnostics?.length" role="status">
        <li v-for="item in display.diagnostics.filter(item => !row.userId || item.userId === row.userId)" :key="item.recordedAt + item.stepId">
          {{ historyTime(item.recordedAt) }} · {{ item.stepId }} · {{ item.phase }} · {{ historyReason(item.reasonCode) }} ({{ item.exceptionType }})
        </li>
      </ul>
      <p v-if="error" role="alert">
        {{ t(error) }}
        <button @click="load">{{ t('重试') }}</button>
      </p>
      <dl class="history-detail-metadata">
        <div>
          <dt>{{ t('结果') }}</dt>
          <dd>
            <nexus-badge :tone="summary.tone">{{ summary.label }}</nexus-badge>
          </dd>
        </div>
        <div>
          <dt>{{ t('运行模式') }}</dt>
          <dd>
            {{
              display?.mode === 'manual' ? t('手动运行') : display?.mode === 'auto' ? t('自动运行') : t('未知')
            }}
          </dd>
        </div>
        <div>
          <dt>{{ t('运行用户') }}</dt>
          <dd>{{ userName }}</dd>
        </div>
        <div>
          <dt>{{ t('运行调用次数') }}</dt>
           <dd>{{ steps.reduce((count, step) => count + startedInvocations(step).length, 0) }} /
            {{ steps.reduce((count, step) => count + (step.maximumInvocations || 1), 0) }}</dd>
        </div>
        <div>
          <dt>{{ t('开始时间') }}</dt>
          <dd>{{ historyTime(display?.startedAt) }}</dd>
        </div>
        <div>
          <dt>{{ t('结束时间') }}</dt>
          <dd>{{ historyTime(row.endedAt) }}</dd>
        </div>
        <div>
          <dt>{{ t('运行总耗时') }}</dt>
          <dd>{{ historyDuration(row.durationMilliseconds, row.durationKind) }}</dd>
        </div>
        <div>
          <dt>{{ t('结果说明') }}</dt>
          <dd>{{ historyReason(finalFacts.reasonCode) }}</dd>
        </div>
      </dl>
      <HistoryTasks :steps="steps" />
      <AttemptDetail
        v-for="item in display || !row.userId ? attempts : []"
        :id="'history-attempt-' + item.attemptId"
        :key="item.attemptId"
        :store="store"
        :run-id="row.runId"
        :attempt-id="item.attemptId"
        :visible="visible && active"
        :ordinal="item.ordinal"
        :facts="item.facts"
        :timing="timing(item.attemptId)"
        :step-names="display?.steps"
        :prepared-report="item.attemptId === report?.attemptId ? report : undefined"
        :step-ids="row.userId ? display?.accounts.find(account => account.userId === row.userId)?.stepIds || [] : undefined"
        v-bind="row.userId ? { userId: row.userId } : {}"
        @focus="emit('attempt', item.attemptId)"
      />
    </div>
    <button slot="footer" @click="close">{{ t('关闭') }}</button>
  </nexus-modal>
</template>
