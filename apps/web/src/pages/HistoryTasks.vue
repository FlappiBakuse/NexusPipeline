<script setup lang="ts">
import { computed } from 'vue';
import type { ActiveStep } from '../control/execution';
import type { HistoryDisplay } from '../control/records';
import { t, locale, registerMessages } from '../app/locale';
import { taskTitle } from '../control/extension-metadata';
import { historyTaskStatus } from './history-row';
import { historyReason } from './history-detail';
import TaskCards from './TaskCards.vue';
import TaskSummary from './TaskSummary.vue';

registerMessages({ 最后一次尝试: 'Final attempt', 任务结果汇总: 'Task outcome summary', '正在读取任务结果…': 'Loading task results…' });
const props = defineProps<{ steps: readonly ActiveStep[]; display?: Pick<HistoryDisplay, 'steps' | 'accounts'>; loading?: boolean }>();
const groups = computed(() => {
  const scripts = new Map(props.display?.steps.map(step => [step.stepId, step.scriptName]));
  const accounts = new Map(props.display?.accounts.map(account => [account.userId, account.name]));
  return props.steps.filter(step => step.tasks.length).map(step => ({
    id: step.stepId,
    script: scripts.get(step.stepId) || step.scriptId,
    account: step.userId ? accounts.get(step.userId) : undefined,
    tasks: step.tasks.map(task => {
      const code = task.isSelected && task.isEnabled
        ? task.reasonCode ?? ((step.phase === 'skipped' || step.phase === 'notExecuted') ? step.reasonCode : undefined)
        : undefined;
      return { id: task.taskId, title: taskTitle(task, locale.value), ...historyTaskStatus(task, step),
        ...(code ? { reason: historyReason(code) } : {}) };
    })
  }));
});
const tasks = computed(() => groups.value.flatMap(group => group.tasks));
</script>
<template>
  <section class="history-tasks">
    <header><h3>{{ t('专项任务') }}</h3><span v-if="groups.length">{{ t('最后一次尝试') }}</span></header>
    <nexus-spinner v-if="loading" :label="t('正在读取任务结果…')" />
    <p v-else-if="!groups.length">{{ t('本次运行没有可用的任务明细') }}</p>
    <template v-else>
      <TaskSummary :tasks="tasks" :label="t('任务结果汇总')" />
      <section v-for="group in groups" :key="group.id" class="task-group">
        <header>
          <h4>{{ group.script }}</h4>
          <span v-if="group.account">{{ group.account }}</span>
        </header>
        <TaskCards :tasks="group.tasks" :label="group.account ? group.script + ' · ' + group.account : group.script" />
      </section>
    </template>
  </section>
</template>
<style scoped>
.history-tasks, .task-group { display: grid; gap: 9px; min-width: 0; }
.history-tasks { margin: 20px 0 34px; }
header { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 9px; }
h3, h4 { margin: 0; font-size: 14px; overflow-wrap: anywhere; }
h4 { font-size: 13px; }
p, header span { margin: 0; font-size: 12px; color: var(--muted); overflow-wrap: anywhere; }
.task-group + .task-group {
  padding-block-start: 10px;
  border-block-start: 1px solid var(--border);
}
</style>
