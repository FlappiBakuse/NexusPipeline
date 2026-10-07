<script setup lang="ts">
import NxpOverflowText from "../../../ui/primitives/NxpOverflowText.vue";
import { computed, nextTick, ref, watch } from "vue";
import { getLocale, t } from "../../../platform/i18n";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import NxpDismissibleNotice from "../../../ui/composites/NxpDismissibleNotice.vue";
import TaskPlanItem from "./TaskPlanItem.vue";
import { resolveTaskText } from "../utils/taskText";
import { taskDiagnosticLabel, currentTaskReport, providerTaskReport, dailyTaskReport, taskStatusLabel } from "../utils/taskLabels";
import type { TaskConfigCheck, TaskDefinition, TaskPlan, TaskReport } from "../utils/taskTypes";
const props = defineProps<{
  plan?: TaskPlan;
  report?: TaskReport;
  layout?: "cards" | "steps";
  stale?: boolean;
}>();
const report = computed(() => currentTaskReport(props.report) ? props.report : undefined);
const plan = computed(() => props.report ? report.value?.originalPlan : props.plan);
const tasks = computed(() => (plan.value?.tasks || []).filter(task => task.enabled).slice().sort((a, b) => a.order - b.order));
const roots = computed(() => tasks.value.filter(task => !task.parentId || !tasks.value.some(parent => parent.id === task.parentId)));
const diagnostics = computed(() => {
  const unique = new Map<string, TaskPlan['diagnostics'][number]>();
  for (const item of [...(plan.value?.diagnostics || []), ...(report.value?.diagnostics || [])]) {
    const owner = plan.value?.tasks.find(task => task.id === item.taskId);
    if (owner && !owner.enabled) continue;
    unique.set(JSON.stringify([item.code, item.taskId, item.message, item.reasonText]), item);
  }
  return [...unique.values()];
});
const noticeDismissed = ref(false);
watch(() => [props.plan, props.stale], () => { noticeDismissed.value = false; });
const focusTaskId = ref("");
const businessTasks = computed(() => tasks.value.filter(task => !task.parentId && task.role === 'business' && task.countsAsUnit !== false));
// Preview counts come from the plan; completed reports use the frozen Host summary.
const businessTotal = computed(() => report.value?.summary?.counts.total ?? businessTasks.value.length);
const finished = computed(() => report.value?.summary?.counts.total !== undefined
  ? (report.value?.summary?.counts.succeeded || 0) + (dailyTaskReport(report.value) ? 0 : (report.value?.summary?.counts.skipped || 0))
  : businessTasks.value.filter(task => ['succeeded', 'skipped'].includes(status(task.id))).length);
const unverified = computed(() => report.value?.summary?.counts.unknown || 0);
const activeAssessment = computed(() => report.value?.admissionBlocked?.configAssessment || plan.value?.configAssessment);
const activeReadiness = computed(() => report.value?.admissionBlocked?.readiness || plan.value?.currentReadiness);
const configChecks = computed(() => (activeAssessment.value?.checks || []).filter(check =>
  check.evaluation !== 'satisfied' && check.evaluation !== 'not_applicable'));
const readiness = activeReadiness;
const readinessStale = computed(() => props.stale || readiness.value?.stale);
const unattributed = computed(() => {
  const latest = new Map<string, NonNullable<TaskReport['incidents']>[number]>();
  for (const event of report.value?.incidents || [])
    if (event.incident.taskId === null) latest.set(event.attemptId + ':' + event.incident.id, event);
  return providerTaskReport(report.value) ? [...latest.values()] : [];
});
function taskName(task: TaskDefinition) {
  return task.nameText ? resolveTaskText(task.nameText, report.value?.displaySnapshot || plan.value?.displaySnapshot, getLocale(), task.name) : task.name;
}

const panel = ref<HTMLElement | null>(null);
watch(() => report.value, async report => {
  focusTaskId.value = "";
  const query = new URLSearchParams(window.location.hash.split("?")[1] || "");
  if (!report || query.get("recordId") !== report.runId || !query.get("taskId")) return;
  focusTaskId.value = query.get("taskId") || "";
  await nextTick();
  const task = Array.from(panel.value?.querySelectorAll<HTMLElement>("[data-task-id]") || [])
    .find(element => element.dataset.taskId === query.get("taskId"));
  if (!task) return;
  const attempt = Array.from(task.querySelectorAll<HTMLElement>("[data-attempt-id]"))
    .find(element => element.dataset.attemptId === query.get("attemptId") && element.closest('[data-task-id]') === task);
  const focus = attempt || task.querySelector<HTMLElement>("button");
  focus?.focus();
  focus?.scrollIntoView?.({ block: "nearest" });
}, { immediate: true });
function status(id: string) { return report.value?.finalTaskResults.find(r => r.taskId === id)?.status || "pending"; }
function incidentEvidence(attemptId: string, sourceId: string, epoch: number, sequence: number) {
  return report.value?.evidenceLines?.find(line => line.attemptId === attemptId && line.sourceId === sourceId && line.epoch === epoch && line.sequence === sequence)?.text || t('tasks.evidence_unavailable');
}
function diagnosticOwnerName(id?: string | null) {
  const task = plan.value?.tasks.find(task => task.id === id);
  return task ? taskName(task) : '';
}
function tone(value: string) { return value === "succeeded" || value === "skipped" ? "ok" : value === "failed" ? "bad" : value === "partial" ? "warn" : value === "running" ? "blue" : "muted"; }
function readinessTone(value?: string) { return value === 'ready' ? 'ok' : value === 'blocked' ? 'bad' : value === 'attention' ? 'warn' : 'muted'; }
function readinessLabel(value?: string) { return t(`tasks.readiness.${value || 'unknown'}`); }
function configCheckText(check: TaskConfigCheck) {
  return check.reasonText
    ? resolveTaskText(check.reasonText, report.value?.displaySnapshot || plan.value?.displaySnapshot, getLocale(), t('tasks.config.setting.default'))
    : t('tasks.config.setting.default');
}
function configCheckLabel(check: TaskConfigCheck) { return t(`tasks.config.evaluation.${check.evaluation}`); }
function configSettingHint(check: TaskConfigCheck) {
  return t(`tasks.config.setting.${check.ruleId}`, {}, t('tasks.config.setting.default'));
}
</script>
<template>
  <p v-if="props.report && !report" role="status">{{ t('tasks.unsupported_report') }}</p>
  <section ref="panel" class="task-report" :aria-label="t('tasks.title')">
    <NxpDismissibleNotice v-if="!report && plan" :visible="!noticeDismissed && Boolean(stale || plan.coverage !== 'complete')" :close-label="t('common.close')" @dismiss="noticeDismissed = true">
      <p v-if="stale" class="task-warning-copy">{{ t('tasks.stale') }}</p>
      <p v-if="plan.coverage !== 'complete'" class="task-warning-copy">{{ t('tasks.coverage_help') }}</p>
    </NxpDismissibleNotice>
    <header class="task-report-header">
      <div><h3>{{ t('tasks.title') }}</h3><p v-if="plan">{{ t(report ? 'tasks.progress' : 'tasks.enabled_count', { count: businessTotal, done: finished }) }}</p></div>
      <NxpBadge v-if="plan && layout !== 'steps'" :tone="plan.coverage === 'complete' ? 'ok' : 'muted'">{{ t(`tasks.coverage.${plan.coverage}`) }}</NxpBadge>
    </header>
    <p v-if="!plan" class="muted">{{ t('tasks.plan_unavailable') }}</p>
    <template v-else>
      <div v-if="report?.admissionBlocked" class="task-admission-blocked" role="alert">
        <strong>{{ t('tasks.admission_blocked') }}</strong>
        <span>{{ t('tasks.admission_blocked_help') }}</span>
      </div>
      <section v-if="!report && activeAssessment" class="task-config-assessment" :aria-label="t('tasks.config.title')">
        <header class="task-config-header">
          <strong>{{ t('tasks.config.title') }}</strong>
          <NxpBadge :tone="readinessStale ? 'muted' : readinessTone(readiness?.state)">{{ readinessLabel(readiness?.state) }}</NxpBadge>
        </header>
        <p v-if="readinessStale" class="task-config-meta">{{ t('tasks.config.stale') }}</p>
        <ul v-if="configChecks.length" class="task-config-list">
          <li v-for="check in configChecks" :key="check.ruleId + ':' + check.scope.kind + ':' + (check.scope.taskId || '')" :data-config-rule="check.ruleId">
            <div class="task-config-row">
              <span>{{ configCheckText(check) }}</span>
              <NxpBadge :tone="check.executionEffect === 'block' ? 'bad' : check.executionEffect === 'warn' ? 'warn' : 'muted'">{{ configCheckLabel(check) }}</NxpBadge>
            </div>
            <small class="task-config-location">{{ configSettingHint(check) }}</small>
          </li>
        </ul>
        <p v-else class="task-config-meta">{{ t('tasks.config.clear') }}</p>
      </section>
      <p v-if="!tasks.length" class="muted">{{ t('tasks.empty') }}</p>
      <div v-if="layout !== 'steps'" class="task-list">
        <TaskPlanItem v-for="task in roots" :key="task.id" :task="task" :tasks="plan.tasks" :report="report" :display-snapshot="report?.displaySnapshot || plan.displaySnapshot" :focus-task-id="focusTaskId" />
      </div>
      <NxpScrollArea v-else-if="layout === 'steps'" direction="horizontal" :aria-label="t('tasks.title')">
        <ol class="task-steps">
          <li v-for="(task, index) in businessTasks" :key="task.id" :data-task-id="task.id" class="task-step">
            <span class="task-marker" :data-status="status(task.id)" aria-hidden="true">{{ index + 1 }}</span>
            <span class="task-step-name"><NxpOverflowText :label="taskName(task)" /></span>
            <NxpBadge class="task-step-status" :tone="tone(status(task.id))">{{ taskStatusLabel(status(task.id), report) }}</NxpBadge>
          </li>
        </ol>
      </NxpScrollArea>
      <p v-if="report && layout !== 'steps' && plan.coverage !== 'complete'" class="task-footnote">{{ t('tasks.coverage_help') }}</p>
      <div v-if="layout !== 'steps' && unattributed.length" class="task-footnote">
        <p>{{ t('tasks.incident.unattributed') }}</p>
        <div v-for="event in unattributed" :key="event.attemptId + ':' + event.incident.id">
          <p>{{ event.incident.reasonText
            ? resolveTaskText(event.incident.reasonText, report?.displaySnapshot, getLocale(), event.incident.reasonCode) : event.incident.reasonCode }}</p>
          <figure v-for="proof in event.incident.evidence" :key="`${proof.sourceId}:${proof.epoch}:${proof.sequence}:${proof.ruleId}`">
            <figcaption>{{ t('tasks.evidence') }} · {{ proof.sourceId }} / {{ proof.epoch }} / {{ proof.sequence }}</figcaption>
            <pre>{{ incidentEvidence(event.attemptId, proof.sourceId, proof.epoch, proof.sequence) }}</pre>
          </figure>
        </div>
      </div>
      <p v-if="providerTaskReport(report) && report?.summary?.recovered && layout !== 'steps'" class="task-notice">{{ t('tasks.recovered') }}</p>
      <p v-if="report && unverified && report.lifecycleOutcome === 'completed'" class="task-footnote">{{ t('tasks.outcome.unverified_count', { count: unverified }) }}</p>
      <ul v-if="diagnostics.length" class="task-plan-notes" :aria-label="t('tasks.plan_notes')">
        <li v-for="(item, index) in diagnostics" :key="index">
          <span v-if="diagnosticOwnerName(item.taskId)">{{ diagnosticOwnerName(item.taskId) }} · </span>
          {{ taskDiagnosticLabel(item, report?.displaySnapshot || plan.displaySnapshot) }}
        </li>
      </ul>
    </template>
  </section>
</template>
<style scoped>
.task-report { display: grid; gap: 8px; min-width: 0; overflow-wrap: anywhere; }
.task-admission-blocked, .task-config-assessment { margin: 0; padding: 12px; border: 1px solid var(--nx-color-border); border-radius: var(--radius-lg, var(--nx-radius-lg)); background: var(--content-card-soft); }
.task-config-assessment { padding-inline: var(--space-5, var(--nx-space-5)); }
.task-admission-blocked { display: grid; gap: 4px; color: var(--bad); }
.task-admission-blocked span { color: var(--nx-color-muted); font-size: 12px; }
.task-config-header, .task-config-row { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
.task-config-header { min-height: 24px; }
.task-config-list { display: grid; gap: 6px; margin: 10px 0 0; padding: 0; list-style: none; }
.task-config-list li { padding: 8px 0; border-top: 1px solid var(--nx-color-border); }
.task-config-list li:first-child { border-top: 0; }
.task-config-list span { min-width: 0; overflow-wrap: anywhere; }
.task-config-list small, .task-config-meta { color: var(--nx-color-muted); font-size: 12px; }
.task-config-list small { display: block; margin-top: 3px; }
.task-config-location { overflow-wrap: anywhere; font-family: var(--nx-font-mono, ui-monospace, monospace); }
.task-config-meta { margin: 8px 0 0; }
.task-plan-notes { margin: 0; padding-inline-start: 20px; font-size: 12px; line-height: 1.6; color: var(--nx-color-muted); overflow-wrap: anywhere; }
.task-footnote figure { margin: 8px 0; }
.task-footnote pre { white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; }
.task-warning-copy { margin: 0; } .task-warning-copy + .task-warning-copy { margin-top: 6px; }
.task-report-header { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
h3 { margin: 0; font-size: 14px; } .task-report-header p { margin: 5px 0 0; color: var(--nx-color-muted); font-size: 12px; }
.task-list { display: grid; gap: 8px; }
.task-marker { flex: 0 0 28px; height: 28px; display: grid; place-items: center; border-radius: 50%; background: var(--muted-soft); color: var(--nx-color-muted); font-size: 12px; font-weight: 700; }
.task-marker[data-status="succeeded"], .task-marker[data-status="skipped"] { background: var(--ok-soft); color: var(--ok); }
.task-marker[data-status="failed"] { background: var(--bad-soft); color: var(--bad); }
.task-marker[data-status="running"] { background: var(--accent-soft); color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft); }
.task-footnote, .task-notice { font-size: 12px; line-height: 1.6; color: var(--nx-color-muted); margin: 0; }
.task-notice { color: var(--ok); }
.task-steps { display: flex; margin: 0; padding: 4px 0 12px; gap: 16px; list-style: none; }
.task-step { box-sizing: border-box; flex: 0 0 200px; width: 200px; height: 88px; min-width: 0; position: relative; display: grid; grid-template-columns: 28px minmax(0, 1fr); grid-template-rows: 28px 24px; align-content: center; align-items: center; gap: 6px 10px; padding: 12px; border: 1px solid var(--nx-color-border); border-radius: var(--radius-lg, var(--nx-radius-lg)); background: var(--content-card-soft); }
.task-step:not(:last-child)::after { content: ''; position: absolute; top: 43px; left: 100%; width: 17px; border-top: 2px solid var(--nx-color-border); }
.task-step-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13px; font-weight: 600; }
.task-step-status { grid-column: 2; justify-self: start; max-width: 100%; box-sizing: border-box; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
