<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { getLocale, t } from '../../../platform/i18n';
import NxpCollapsibleCard from '../../../ui/composites/NxpCollapsibleCard.vue';
import NxpBadge from '../../../ui/primitives/NxpBadge.vue';
import TaskEvidence from './TaskEvidence.vue';
import { dailyTaskReport, taskStatusLabel } from '../utils/taskLabels';
import { hasTaskProof, taskReason } from '../utils/taskPresentation';
import { resolveTaskText } from '../utils/taskText';
import type { TaskConfigCheck, TaskDefinition, TaskReport, TaskResult, TaskDisplaySnapshot } from '../utils/taskTypes';

const props = withDefaults(defineProps<{
  task: TaskDefinition;
  tasks: TaskDefinition[];
  ancestors?: string[];
  report?: TaskReport;
  displaySnapshot?: TaskDisplaySnapshot;
  focusTaskId?: string;
  configChecks?: TaskConfigCheck[];
}>(), { ancestors: () => [], configChecks: () => [] });
const expanded = ref(false);
const children = computed(() => props.tasks.filter(task => task.enabled && task.parentId === props.task.id && task.id !== props.task.id && !props.ancestors.includes(task.id)).slice().sort((a, b) => a.order - b.order));
const isParent = computed(() => props.tasks.some(task => task.parentId === props.task.id));
const name = computed(() => resolveTaskText(props.task.nameText, props.displaySnapshot, getLocale(), props.task.name));
const configIssue = computed(() => props.configChecks.some(check => check.scope.taskId === props.task.id));
const finalResult = computed(() => props.report?.finalTaskResults.find(result => result.taskId === props.task.id));
const status = computed(() => finalResult.value?.status || 'pending');
function resultLabel(result?: TaskResult) {
  if (props.report && !result) return t('tasks.presentation.result_missing');
  if (props.report?.resultBasis === 'framework-task-v1') return taskStatusLabel(result?.status || 'pending', props.report);
  if (result?.engineStatus === 'succeeded') return t('tasks.presentation.upstream_completed');
  if (result?.engineStatus === 'failed') return t('tasks.presentation.upstream_failed');
  return taskStatusLabel(result?.status || 'pending', props.report);
}
const tone = computed(() => finalResult.value?.engineStatus === 'failed' ? 'bad' : ['succeeded', 'skipped'].includes(status.value) ? 'ok' : status.value === 'failed' ? 'bad' : status.value === 'partial' ? 'warn' : status.value === 'running' ? 'blue' : 'muted');
const childSummary = computed(() => {
  if (!children.value.length) return isParent.value && !props.report ? t('tasks.presentation.no_enabled_children') : '';
  if (!props.report) return t('tasks.presentation.enabled_children', { count: children.value.length });
  const complete = children.value.filter(task => props.report!.finalTaskResults.some(result => result.taskId === task.id && ['succeeded', 'skipped'].includes(result.status))).length;
  return t('tasks.presentation.child_results', { complete, total: children.value.length });
});
function incidents(attemptId: string) {
  const latest = new Map<string, NonNullable<TaskReport['incidents']>[number]['incident']>();
  for (const event of props.report?.incidents || [])
    if (event.attemptId === attemptId && event.incident.taskId === props.task.id) latest.set(event.incident.id, event.incident);
  return dailyTaskReport(props.report) ? [] : [...latest.values()];
}
function ownResult(result: TaskResult) {
  return !isParent.value || result.ownStatus !== 'pending' || hasTaskProof(result);
}
type AttemptRow = { attempt: TaskReport['attemptReports'][number]; results: TaskResult[]; omitted: boolean };
const rows = computed(() => {
  const output: AttemptRow[] = [];
  for (const attempt of props.report?.attemptReports || []) {
    const results = attempt.taskResults.filter(result => result.taskId === props.task.id && ownResult(result));
    const events = incidents(attempt.attemptId);
    if (isParent.value && !results.length && !events.length) continue;
    output.push({ attempt, results, omitted: !results.length && !events.length && !attempt.selectedTaskIds.includes(props.task.id) });
  }
  return output;
});
const attemptSummary = computed(() => {
  if (!props.report) return '';
  const attempts = props.report.attemptReports;
  const finalAttempt = finalResult.value && attempts.find(item => item.attemptId === finalResult.value?.lastAttemptId);
  if (finalAttempt) return t('tasks.presentation.attempt_result', { number: finalAttempt.number, status: resultLabel(finalResult.value) });
  for (const attempt of attempts.slice().sort((a, b) => b.number - a.number)) {
    const result = attempt.taskResults.find(item => item.taskId === props.task.id);
    if (result || attempt.selectedTaskIds.includes(props.task.id))
      return t('tasks.presentation.attempt_result', { number: attempt.number, status: resultLabel(result) });
  }
  const latest = attempts.slice().sort((a, b) => b.number - a.number)[0];
  if (latest) return t('tasks.presentation.attempt_result', { number: latest.number,
    status: t(isParent.value ? 'tasks.presentation.no_independent_attempt' : 'tasks.presentation.not_scheduled_single') });
  return t('tasks.presentation.no_attempt_record');
});
const description = computed(() => [attemptSummary.value, childSummary.value].filter(Boolean).join(' · '));
function fact(group: string, value: string) { return t(`tasks.presentation.${group}.${value}`, {}, t(`tasks.presentation.${group}.unknown`)); }
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
  <NxpCollapsibleCard class="task-card" :title="name" :description="description" :expanded="expanded" :flatten-at-limit="true" :data-task-id="task.id" @toggle="expanded = $event">
    <template #actions>
      <NxpBadge v-if="report" :tone="tone">{{ resultLabel(finalResult) }}</NxpBadge>
      <NxpBadge v-else-if="configIssue" tone="warn">{{ t('tasks.presentation.config_issue') }}</NxpBadge>
    </template>
    <p v-if="!report && configIssue" class="muted">{{ t('tasks.presentation.config_hint') }}</p>
    <div v-if="children.length" class="task-plan-children">
      <TaskPlanItem v-for="child in children" :key="child.id" :task="child" :tasks="tasks" :report="report" :display-snapshot="displaySnapshot" :focus-task-id="focusTaskId" :ancestors="[...ancestors, task.id]" :config-checks="configChecks" />
    </div>
    <dl v-else-if="!report && !isParent" class="task-plan-facts">
      <div><dt>{{ t('tasks.presentation.detection') }}</dt><dd>{{ fact('detection', task.detection) }}</dd></div>
      <div><dt>{{ t('tasks.presentation.retry_risk') }}</dt><dd>{{ fact('risk', task.retryRisk) }}</dd></div>
      <div><dt>{{ t('tasks.presentation.role') }}</dt><dd>{{ fact('role', task.role) }}</dd></div>
    </dl>
      <div v-for="row in rows" :key="row.attempt.attemptId" class="task-attempt" :data-attempt-id="row.attempt.attemptId" tabindex="-1">
        <div class="task-attempt-heading">
          <strong>{{ t('tasks.attempt', { number: row.attempt.number }) }}</strong>
          <span v-if="row.omitted" class="muted">{{ t('tasks.presentation.not_scheduled_single') }}</span>
          <span v-if="!row.results.length && row.attempt.selectedTaskIds.includes(task.id)" class="muted">{{ t('tasks.presentation.result_unavailable') }}</span>
          <span v-for="result in row.results" :key="result.taskId">{{ resultLabel(result) }} · {{ taskReason(result.reasonCode, result.reasonText, displaySnapshot) }}</span>
        </div>
        <div v-for="result in row.results" :key="result.taskId">
          <TaskEvidence v-if="report" :proof="result" :report="report" :attempt-id="row.attempt.attemptId" />
        </div>
        <div v-for="incident in incidents(row.attempt.attemptId)" :key="incident.id" class="task-incident">
          <p>{{ t('tasks.incident.title') }} · {{ t(`tasks.incident.${incident.resolution}`) }} · {{ taskReason(incident.reasonCode, incident.reasonText, displaySnapshot) }}</p>
          <TaskEvidence v-if="report" :proof="incident" :report="report" :attempt-id="row.attempt.attemptId" />
        </div>
      </div>
  </NxpCollapsibleCard>
</template>

<style scoped>
.task-card[data-nxp-surface-kind="card"][data-nxp-surface-tone="a"] { background: var(--content-card-soft); }
.task-card[data-nxp-surface-kind="card"][data-nxp-surface-tone="b"] { background: var(--nx-color-surface); }
.task-card { --nx-collapsible-header-height: calc(var(--nx-control-height) + var(--nx-space-4)); --nx-collapsible-header-padding: var(--nx-space-3); --nx-collapsible-body-padding: var(--nx-space-5); }
.task-card[data-nxp-surface-kind="group"] > :deep(.nxp-collapsible-card-body) { padding-inline-start: var(--nx-space-2); }
.task-attempt { font-size: 12px; }
.task-attempt-heading { display: flex; flex-wrap: wrap; align-items: baseline; gap: var(--nx-space-1) var(--nx-space-2); line-height: 1.6; }
.task-attempt + .task-attempt, .task-plan-children + .task-attempt { margin-top: var(--nx-space-3); padding-top: var(--nx-space-3); border-top: 1px solid var(--nx-color-border); }
.task-attempt p { line-height: 1.6; }
.task-plan-children { display: grid; gap: var(--nx-space-2); }
.task-plan-facts { display: grid; gap: var(--nx-space-3); margin: 0; font-size: 12px; }
.task-plan-facts > div { min-width: 0; overflow-wrap: anywhere; }
dt { color: var(--nx-color-muted); } dd { margin: var(--nx-space-1) 0 0; }
</style>
