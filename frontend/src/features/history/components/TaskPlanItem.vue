<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { getLocale, t } from "../../../platform/i18n";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import { resolveTaskText } from "../utils/taskText";
import type { TaskDefinition, TaskReport, TaskResult, TaskDisplaySnapshot, TaskTextRef } from "../utils/taskTypes";

const props = withDefaults(defineProps<{
  task: TaskDefinition;
  tasks: TaskDefinition[];
  depth?: number;
  ancestors?: string[];
  report?: TaskReport;
  displaySnapshot?: TaskDisplaySnapshot;
  focusTaskId?: string;
}>(), { depth: 0, ancestors: () => [] });
const expanded = ref(false);
const children = computed(() => props.tasks.filter(task => task.enabled && task.parentId === props.task.id && task.id !== props.task.id && !props.ancestors.includes(task.id)).slice().sort((a, b) => a.order - b.order));
const isParent = computed(() => props.tasks.some(task => task.parentId === props.task.id));
const name = computed(() => props.task.nameText ? resolveTaskText(props.task.nameText, props.displaySnapshot, getLocale(), props.task.name) : props.task.name);
function label(group: string, value: string) { return t(`tasks.${group}.${value}`, {}, t(`tasks.${group}.unknown`)); }
const status = computed(() => props.report?.finalTaskResults.find(result => result.taskId === props.task.id)?.status || 'pending');
const engineStatus = computed(() => props.report?.finalTaskResults.find(result => result.taskId === props.task.id)?.engineStatus);
const engineLabel = computed(() => engineStatus.value === 'succeeded'
  ? (getLocale().startsWith('zh') ? '上游报告完成 · 业务未核验' : 'Upstream completed · business unverified')
  : engineStatus.value === 'failed' ? (getLocale().startsWith('zh') ? '上游引擎失败' : 'Upstream engine failed') : '');
const tone = computed(() => ['succeeded', 'skipped'].includes(status.value) ? 'ok' : status.value === 'failed' ? 'bad' : status.value === 'partial' ? 'warn' : status.value === 'running' ? 'blue' : 'muted');
function reason(code: string, text?: TaskTextRef) { return text ? resolveTaskText(text, props.displaySnapshot, getLocale(), code) : t(`tasks.reason.${code}`, {}, code); }
function incidents(attemptId: string) {
  const latest = new Map<string, NonNullable<TaskReport['incidents']>[number]['incident']>();
  for (const event of props.report?.incidents || [])
    if (event.attemptId === attemptId && event.incident.taskId === props.task.id) latest.set(event.incident.id, event.incident);
  return [...latest.values()];
}
function evidenceText(attemptId: string, evidence: TaskResult['evidence'][number]) {
  return props.report?.evidenceLines?.find(line => line.attemptId === attemptId && line.sourceId === evidence.sourceId && line.epoch === evidence.epoch && line.sequence === evidence.sequence)?.text || t('tasks.evidence_unavailable');
}
watch(() => props.focusTaskId, id => {
  const visited = new Set<string>();
  while (id && !visited.has(id)) {
    if (id === props.task.id) { expanded.value = true; return; }
    visited.add(id);
    id = props.tasks.find(task => task.id === id)?.parentId || undefined;
  }
}, { immediate: true });
</script>

<template>
  <NxpCollapsibleCard class="task-card" :title="name" :expanded="expanded" :surface="depth % 2 === 0 ? 'secondary' : 'default'" :data-task-id="task.id" @toggle="expanded = $event">
    <template #actions><NxpBadge :tone="engineStatus === 'failed' ? 'bad' : tone">{{ engineLabel || t(`tasks.status.${status}`) }}</NxpBadge></template>
    <div v-if="children.length" class="task-plan-children">
      <TaskPlanItem v-for="child in children" :key="child.id" :task="child" :tasks="tasks" :report="report" :display-snapshot="displaySnapshot" :focus-task-id="focusTaskId" :depth="depth + 1" :ancestors="[...ancestors, task.id]" />
    </div>
    <dl v-else-if="!isParent" class="task-plan-facts" :class="{ 'is-light': depth % 2 === 1 }">
      <div><dt>{{ t('tasks.detection') }}</dt><dd>{{ label('detection', task.detection) }}</dd></div>
      <div><dt>{{ t('tasks.risk') }}</dt><dd>{{ label('risk', task.retryRisk) }}</dd></div>
      <div><dt>{{ t('tasks.role') }}</dt><dd>{{ label('role', task.role) }}</dd></div>
    </dl>
    <div v-for="attempt in report?.attemptReports || []" :key="attempt.attemptId" class="task-attempt" :data-attempt-id="attempt.attemptId" tabindex="-1">
      <strong>{{ t('tasks.attempt', { number: attempt.number }) }}</strong>
      <p v-if="!attempt.selectedTaskIds.includes(task.id)" class="muted">{{ t('tasks.not_retried') }}</p>
      <template v-for="result in attempt.taskResults.filter(result => result.taskId === task.id)" :key="result.taskId">
        <p>{{ t(`tasks.status.${result.status}`) }} · {{ reason(result.reasonCode, result.reasonText) }}</p>
        <figure v-for="id in result.structuredEvidenceRefs || []" :key="id" class="task-evidence">
          <figcaption>{{ getLocale().startsWith('zh') ? '结构化引擎证据' : 'Structured engine evidence' }}</figcaption>
          <pre>{{ JSON.stringify(report?.structuredEvidence?.find(item => item.id === id), null, 2) }}</pre>
        </figure>
        <figure v-for="(evidence, index) in result.evidence" :key="`${evidence.sourceId}:${evidence.epoch}:${evidence.sequence}`" class="task-evidence" :class="{ 'is-light': depth % 2 === 0 }">
          <figcaption>{{ t('tasks.evidence') }} {{ index + 1 }} · {{ evidence.sourceId }} / {{ evidence.epoch }} / {{ evidence.sequence }}</figcaption>
          <pre>{{ evidenceText(attempt.attemptId, evidence) }}</pre>
        </figure>
      </template>
      <div v-for="incident in incidents(attempt.attemptId)" :key="incident.id" class="task-incident">
        <p>{{ t('tasks.incident.title') }} · {{ t(`tasks.incident.${incident.resolution}`) }} · {{ reason(incident.reasonCode, incident.reasonText) }}</p>
        <figure v-for="(evidence, index) in incident.evidence" :key="`${evidence.sourceId}:${evidence.epoch}:${evidence.sequence}:${evidence.ruleId}`" class="task-evidence">
          <figcaption>{{ t('tasks.evidence') }} {{ index + 1 }} · {{ evidence.sourceId }} / {{ evidence.epoch }} / {{ evidence.sequence }}</figcaption>
          <pre>{{ evidenceText(attempt.attemptId, evidence) }}</pre>
        </figure>
      </div>
      <p v-if="attempt.retryDecision" class="muted">{{ t('tasks.retry') }} · {{ reason(attempt.retryDecision.reasonCode, attempt.retryDecision.reasonText) }}</p>
    </div>
  </NxpCollapsibleCard>
</template>

<style scoped>
.task-card { --nx-collapsible-header-height: 57.6px; --nx-collapsible-header-padding: 12.8px; --nx-collapsible-body-padding: 16px; }
.task-attempt { margin-top: 10px; padding-top: 10px; border-top: 1px solid var(--nx-color-border); font-size: 12px; }
.task-attempt p { line-height: 1.6; }
.task-evidence { margin: 8px 0 0; padding: 10px; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); background: var(--content-card); }
.task-evidence.is-light { background: var(--content-card-soft); }
figcaption { color: var(--nx-color-muted); overflow-wrap: anywhere; }
pre { margin: 8px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12px; line-height: 1.6; }
.task-plan-children { display: grid; gap: 10px; }
.task-plan-facts { display: flex; flex-wrap: wrap; gap: 12px 24px; margin: 0; padding: 12px; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-md); background: var(--content-card); font-size: 12px; }
.task-plan-facts.is-light { background: var(--content-card-soft); }
dt { color: var(--nx-color-muted); } dd { margin: 5px 0 0; }
</style>
