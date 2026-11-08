<script setup lang="ts">
import type { RecordsStore } from '../app/store-ports';
import type { ActiveStep } from '../control/execution';
import type { HistoryImage } from '../control/records';
import AttemptLogs from './AttemptLogs.vue';
import AttemptImages from './AttemptImages.vue';
import { t } from '../app/locale';
import { outcomeName } from './history-row';
import { attemptTitle, historyDuration, historyReason, historyTime, startedInvocations } from './history-detail';
import type { HistoryLog } from './attempt-resources';
const props = defineProps<{ store: RecordsStore; runId: string; attemptId: string; visible: boolean;
  steps: ActiveStep[]; stepNames?: { stepId: string; scriptName: string }[]; images: HistoryImage[];
  logs: HistoryLog[];
  logsReady: boolean; imagesReady: boolean }>();
function logsFor(stepId: string, ordinal: number) {
  return props.logs.filter(log => log.key.stepId === stepId &&
    (log.key.invocationOrdinal == null || log.key.invocationOrdinal === ordinal)).map(log =>
    ({ ...log, key: { ...log.key, invocationOrdinal: ordinal } }));
}
</script>
<template>
  <section v-for="step in steps" :key="step.stepId">
    <h4>{{ stepNames?.find(item => item.stepId === step.stepId)?.scriptName || step.scriptId }}</h4>
    <p v-if="!startedInvocations(step).length">{{ t('尚未启动脚本') }}</p>
    <article v-for="invocation in startedInvocations(step)" :key="invocation.ordinal" class="history-attempt-card">
      <header>
        <h3>{{ attemptTitle(invocation.ordinal) }}</h3>
        <nexus-badge :tone="invocation.business.toLowerCase() === 'succeeded' ? 'success' : invocation.business.toLowerCase() === 'failed' ? 'danger' : 'neutral'">{{ outcomeName(invocation.business.toLowerCase()) }}</nexus-badge>
      </header>
      <dl class="history-detail-metadata">
        <div>
          <dt>{{ t('时间') }}</dt>
          <dd>{{ historyTime(invocation.startedAt) }} - {{ historyTime(invocation.endedAt) }}</dd>
        </div>
        <div>
          <dt>{{ t('耗时') }}</dt>
          <dd>{{ historyDuration(invocation.durationMilliseconds) }}</dd>
        </div>
        <div class="history-attempt-reason">
          <dt>{{ t('原因') }}</dt>
          <dd>{{ historyReason(invocation.reasonCode) }}</dd>
        </div>
      </dl>
      <AttemptLogs v-if="logsReady" :store="store" :run-id="runId" :attempt-id="attemptId" :visible="visible"
        :ordinal="invocation.ordinal" :logs="logsFor(step.stepId, invocation.ordinal)" />
      <AttemptImages v-if="imagesReady" :store="store" :run-id="runId" :attempt-id="attemptId" :visible="visible"
        :images="images.filter(image => image.key.stepId === step.stepId && image.invocationOrdinal === invocation.ordinal)" />
    </article>
  </section>
</template>
