<script setup lang="ts">
import { computed } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import type { ActiveStep } from '../control/execution';
import type { HistoryImage } from '../control/records';
import AttemptLogs from './AttemptLogs.vue';
import AttemptImages from './AttemptImages.vue';
import { t } from '../app/locale';
import { outcomeName, attemptTitle, historyDuration, historyReason, historyTime, startedInvocations } from './history-format';
import type { HistoryLog } from './attempt-resources';
const props = defineProps<{ store: RecordsStore; runId: string; attemptId: string; visible: boolean;
  steps: ActiveStep[]; stepNames?: { stepId: string; scriptName: string }[]; images: HistoryImage[];
  logs: HistoryLog[];
  logsReady: boolean; imagesReady: boolean }>();

type InvocationAssets = { logs: HistoryLog[]; images: HistoryImage[] };
const sections = computed(() => {
  const names = new Map<string, string>();
  for (const { stepId, scriptName } of props.stepNames ?? [])
    if (!names.has(stepId)) names.set(stepId, scriptName);
  const indexed = new Map<string, Map<number, InvocationAssets>>();
  const result = props.steps.map(step => {
    const indexedStep = indexed.get(step.stepId) ?? new Map<number, InvocationAssets>();
    indexed.set(step.stepId, indexedStep);
    const invocations = startedInvocations(step).map(invocation => {
      const assets = indexedStep.get(invocation.ordinal) ?? { logs: [], images: [] };
      indexedStep.set(invocation.ordinal, assets);
      return { invocation, assets };
    });
    return { step, label: names.get(step.stepId) || step.scriptId, invocations };
  });
  if (props.logsReady) for (const log of props.logs) {
    const { stepId, invocationOrdinal } = log.key;
    const indexedStep = indexed.get(stepId);
    if (!indexedStep) continue;
    if (invocationOrdinal == null) {
      for (const [ordinal, assets] of indexedStep)
        assets.logs.push({ ...log, key: { ...log.key, invocationOrdinal: ordinal } });
    } else indexedStep.get(invocationOrdinal)?.logs.push(log);
  }
  if (props.imagesReady) for (const image of props.images)
    indexed.get(image.key.stepId)?.get(image.invocationOrdinal)?.images.push(image);
  return result;
});
</script>
<template>
  <section v-for="{ step, label, invocations } in sections" :key="step.stepId" :aria-label="label">
    <p v-if="!invocations.length">{{ t('尚未启动脚本') }}</p>
    <article v-for="{ invocation, assets } in invocations" :key="invocation.ordinal" class="history-attempt-card">
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
        :ordinal="invocation.ordinal" :logs="assets.logs" />
      <AttemptImages v-if="imagesReady" :store="store" :run-id="runId" :attempt-id="attemptId" :visible="visible"
        :images="assets.images" />
    </article>
  </section>
</template>
