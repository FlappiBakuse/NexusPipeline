<script setup lang="ts">
import { computed } from 'vue';
import type { RunTask } from '../control/execution';
import { t, locale, registerMessages } from '../app/locale';
import { taskTitle } from '../control/extension-metadata';

const props = defineProps<{ tasks: readonly RunTask[] }>();
const shown = computed(() => props.tasks.filter((task) => task.isEnabled));
const labels: Record<string, string> = {
  succeeded: '完成',
  running: '运行中',
  failed: '失败',
  partial: '部分完成',
  skipped: '已跳过',
  unknown: '未确认',
  unverified: '未核验'
};
function state(task: RunTask): string {
  return task.isSelected ? task.state : 'unverified';
}
function label(task: RunTask): string {
  return t(labels[state(task)] || '未执行');
}
function tone(task: RunTask): string {
  const value = state(task);
  return value === 'succeeded' || value === 'skipped'
    ? 'success'
    : value === 'failed'
      ? 'danger'
      : value === 'partial' ? 'warning' : value === 'running'
        ? 'info'
        : 'neutral';
}
registerMessages({
  专项任务: 'Specialized tasks',
  完成: 'Completed',
  部分完成: 'Partially completed',
  未确认: 'Unconfirmed',
  未核验: 'Unverified',
  未执行: 'Not started'
});
</script>

<template>
  <nexus-scroll-area
    v-if="shown.length"
    class="live-task-steps"
    direction="horizontal"
    :label="t('专项任务')"
  >
    <ol>
      <li v-for="(task, index) in shown" :key="task.taskId" :data-task-id="task.taskId">
        <span class="task-number" :data-state="state(task)" aria-hidden="true">{{ index + 1 }}</span>
        <nexus-overflow-text class="task-name" :text="taskTitle(task, locale)" />
        <nexus-badge class="task-state" :tone="tone(task)" :label="label(task)" />
      </li>
    </ol>
  </nexus-scroll-area>
</template>

<style scoped>
.live-task-steps {
  margin-top: 16px;
  min-width: 0;
}
ol {
  display: flex;
  gap: 16px;
  margin: 0;
  padding: 4px 0 12px;
  list-style: none;
}
li {
  position: relative;
  display: grid;
  grid-template-columns: 28px minmax(0, 1fr);
  grid-template-rows: 28px 24px;
  align-content: center;
  align-items: center;
  gap: 6px 10px;
  flex: 0 0 200px;
  width: 200px;
  height: 88px;
  min-width: 0;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--content-card-soft);
}
li:not(:last-child)::after {
  content: '';
  position: absolute;
  left: 100%;
  top: 43px;
  width: 17px;
  border-top: 2px solid var(--border);
}
.task-number {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  background: var(--muted-soft);
  color: var(--muted);
  font-size: 12px;
  font-weight: 700;
}
.task-number[data-state='succeeded'],
.task-number[data-state='skipped'] {
  background: var(--ok-soft);
  color: var(--ok);
}
.task-number[data-state='failed'] {
  background: var(--bad-soft);
  color: var(--bad);
}
.task-number[data-state='running'] {
  background: var(--accent-soft);
  color: var(--accent);
  box-shadow: 0 0 0 3px var(--accent-soft);
}
.task-name {
  min-width: 0;
  font-size: 13px;
  font-weight: 600;
}
.task-state {
  grid-column: 2;
  justify-self: start;
  max-width: 100%;
}
</style>
