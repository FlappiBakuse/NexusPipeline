<script setup lang="ts">
import { computed } from 'vue';
import { getLocale, t } from '../../../platform/i18n';
import { taskReason } from '../utils/taskPresentation';
import { resolveTaskText } from '../utils/taskText';
import type { TaskReport } from '../utils/taskTypes';
const props = defineProps<{ report: TaskReport; attempt: TaskReport['attemptReports'][number] }>();
const decision = computed(() => props.attempt.retryDecision);
const snapshot = computed(() => props.report.displaySnapshot || props.report.originalPlan.displaySnapshot);
function names(ids: string[]) {
  return [...new Set(ids)].map(id => {
    const task = props.report.originalPlan.tasks.find(task => task.id === id);
    return task ? resolveTaskText(task.nameText, snapshot.value, getLocale(), task.name) : t('tasks.presentation.unnamed_task');
  }).join(getLocale().startsWith('zh') ? '、' : ', ');
}
const targets = computed(() => decision.value?.targetTaskIds);
const launchScope = computed(() => decision.value?.launchScopeTaskIds ?? decision.value?.includedTaskIds);
const title = computed(() => t(`tasks.presentation.retry.${decision.value?.decision}`, {}, t('tasks.presentation.retry.unknown')));
</script>

<template>
  <div v-if="decision" class="task-retry-decision">
    <strong>{{ t('tasks.presentation.next_step') }}：{{ title }}</strong>
    <p>{{ t('common.reason') }}：{{ taskReason(decision.reasonCode, decision.reasonText, snapshot) }}</p>
    <template v-if="decision.decision !== 'stop'">
      <p v-if="targets?.length">{{ t('tasks.presentation.retry_targets') }}：{{ names(targets) }}</p>
      <p v-if="launchScope?.length">{{ t('tasks.presentation.launch_scope') }}：{{ names(launchScope) }}</p>
      <p v-else-if="!targets?.length" class="muted">{{ t('tasks.presentation.scope_unavailable') }}</p>
    </template>
  </div>
</template>

<style scoped>
.task-retry-decision { padding-block: var(--nx-space-3); border-block: 1px solid var(--nx-color-border); overflow-wrap: anywhere; font-size: 12px; }
p { margin: var(--nx-space-2) 0; }
</style>
