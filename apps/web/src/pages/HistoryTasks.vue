<script setup lang="ts">
import { computed } from 'vue';
import type { ActiveStep } from '../control/execution';
import { t, locale, registerMessages } from '../app/locale';
import { taskTitle } from '../control/extension-metadata';
import { outcomeName } from './history-row';
const props = defineProps<{ steps: readonly ActiveStep[] }>();
const tasks = computed(() =>
  props.steps.flatMap(step => step.tasks)
);
registerMessages({ 已停用: 'Disabled' });
function label(task: ActiveStep['tasks'][number]): string {
  return !task.isEnabled ? t('已停用') : !task.isSelected ? t('待核实') : outcomeName(task.state);
}
function tone(task: ActiveStep['tasks'][number]): string {
  return !task.isEnabled || !task.isSelected ? 'neutral' : task.state === 'succeeded' ? 'success' :
    task.state === 'failed' ? 'danger' : task.state === 'partial' ? 'warning' : 'neutral';
}
</script>
<template>
  <section class="history-tasks">
    <h3>{{ t('专项任务') }}</h3>
    <p v-if="!tasks.length">{{ t('本次运行没有可用的任务明细') }}</p>
    <ol v-else>
      <li v-for="(task, index) in tasks" :key="String(task.taskId) + index">
        <span>{{ taskTitle(task, locale) }}</span>
        <nexus-badge :tone="tone(task)">
          {{ label(task) }}
        </nexus-badge>
      </li>
    </ol>
  </section>
</template>
