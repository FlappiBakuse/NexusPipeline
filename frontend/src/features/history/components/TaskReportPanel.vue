<script setup lang="ts">
import { computed, nextTick, ref, watch } from "vue";
import { t } from "../../../platform/i18n";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import NxpDismissibleNotice from "../../../ui/composites/NxpDismissibleNotice.vue";
import TaskPlanItem from "./TaskPlanItem.vue";
import { taskNameKeys } from "../utils/taskLabels";
import type { TaskDefinition, TaskPlan, TaskReport } from "../utils/taskTypes";
const props = defineProps<{ plan?: TaskPlan; report?: TaskReport; layout?: "cards" | "steps"; stale?: boolean }>();
const plan = computed(() => props.report?.originalPlan || props.plan);
const tasks = computed(() => (plan.value?.tasks || []).filter(task => task.enabled).slice().sort((a, b) => a.order - b.order));
const roots = computed(() => tasks.value.filter(task => !task.parentId || !tasks.value.some(parent => parent.id === task.parentId)));
const noticeDismissed = ref(false);
watch(() => [props.plan, props.stale], () => { noticeDismissed.value = false; });
const focusTaskId = ref("");
const finished = computed(() => tasks.value.filter(task => ['succeeded', 'skipped'].includes(status(task.id))).length);
function taskName(task: TaskDefinition) {
  // Runtime/custom labels are content: only translate names from the reviewed built-in adapters.
  if (!/^(march7th|zzzonedragon|baah|bettergi):/.test(task.id)) return task.name;
  return t(`tasks.name.${taskNameKeys[task.name] || "custom"}`, {}, task.name);
}

const panel = ref<HTMLElement | null>(null);
watch(() => props.report, async report => {
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
function status(id: string) { return props.report?.finalTaskResults.find(r => r.taskId === id)?.status || "pending"; }
function tone(value: string) { return value === "succeeded" || value === "skipped" ? "ok" : value === "failed" ? "bad" : value === "partial" ? "warn" : value === "running" ? "blue" : "muted"; }
</script>
<template>
  <section ref="panel" class="task-report" :aria-label="t('tasks.title')">
    <NxpDismissibleNotice v-if="!report && plan" :visible="!noticeDismissed && Boolean(stale || plan.coverage !== 'complete')" :close-label="t('common.close')" @dismiss="noticeDismissed = true">
      <p v-if="stale" class="task-warning-copy">{{ t('tasks.stale') }}</p>
      <p v-if="plan.coverage !== 'complete'" class="task-warning-copy">{{ t('tasks.coverage_help') }}</p>
    </NxpDismissibleNotice>
    <header class="task-report-header">
      <div><h3>{{ t('tasks.title') }}</h3><p v-if="plan">{{ t(report ? 'tasks.progress' : 'tasks.enabled_count', { count: tasks.length, done: finished }) }}</p></div>
      <NxpBadge v-if="plan && layout !== 'steps'" :tone="plan.coverage === 'complete' ? 'ok' : 'muted'">{{ t(`tasks.coverage.${plan.coverage}`) }}</NxpBadge>
    </header>
    <p v-if="!plan" class="muted">{{ t('tasks.legacy') }}</p>
    <template v-else>
      <p v-if="report?.summary.recovered && layout !== 'steps'" class="task-notice">{{ t('tasks.recovered') }}</p>
      <p v-if="!tasks.length" class="muted">{{ t('tasks.empty') }}</p>
      <div v-if="layout !== 'steps'" class="task-list">
        <TaskPlanItem v-for="task in roots" :key="task.id" :task="task" :tasks="plan.tasks" :report="report" :focus-task-id="focusTaskId" />
      </div>
      <NxpScrollArea v-else-if="layout === 'steps'" direction="horizontal" :aria-label="t('tasks.title')">
        <ol class="task-steps">
          <li v-for="(task, index) in tasks" :key="task.id" :data-task-id="task.id" class="task-step">
            <span class="task-marker" :data-status="status(task.id)" aria-hidden="true">{{ index + 1 }}</span>
            <span class="task-step-name" :title="taskName(task)">{{ taskName(task) }}</span>
            <NxpBadge class="task-step-status" :tone="tone(status(task.id))">{{ t(`tasks.status.${status(task.id)}`) }}</NxpBadge>
          </li>
        </ol>
      </NxpScrollArea>
      <p v-if="report && layout !== 'steps' && plan.coverage !== 'complete'" class="task-footnote">{{ t('tasks.coverage_help') }}</p>
    </template>
  </section>
</template>
<style scoped>
.task-report { min-width: 0; overflow-wrap: anywhere; }
.task-warning-copy { margin: 0; } .task-warning-copy + .task-warning-copy { margin-top: 6px; }
.task-report-header { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 16px 0 12px; flex-wrap: wrap; }
h3 { margin: 0; font-size: 14px; } .task-report-header p { margin: 5px 0 0; color: var(--nx-color-muted); font-size: 12px; }
.task-list { display: grid; gap: 8px; }
.task-marker { flex: 0 0 28px; height: 28px; display: grid; place-items: center; border-radius: 50%; background: var(--muted-soft); color: var(--nx-color-muted); font-size: 12px; font-weight: 700; }
.task-marker[data-status="succeeded"], .task-marker[data-status="skipped"] { background: var(--ok-soft); color: var(--ok); }
.task-marker[data-status="failed"] { background: var(--bad-soft); color: var(--bad); }
.task-marker[data-status="running"] { background: var(--accent-soft); color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft); }
.task-footnote, .task-notice { font-size: 12px; line-height: 1.6; color: var(--nx-color-muted); margin: 12px 0 0; }
.task-notice { color: var(--ok); }
.task-steps { display: flex; margin: 0; padding: 4px 0 12px; gap: 16px; list-style: none; }
.task-step { box-sizing: border-box; flex: 0 0 200px; width: 200px; height: 88px; min-width: 0; position: relative; display: grid; grid-template-columns: 28px minmax(0, 1fr); grid-template-rows: 28px 24px; align-content: center; align-items: center; gap: 6px 10px; padding: 12px; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); background: var(--content-card-soft); }
.task-step:not(:last-child)::after { content: ''; position: absolute; top: 43px; left: 100%; width: 17px; border-top: 2px solid var(--nx-color-border); }
.task-step-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13px; font-weight: 600; }
.task-step-status { grid-column: 2; justify-self: start; max-width: 100%; box-sizing: border-box; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
