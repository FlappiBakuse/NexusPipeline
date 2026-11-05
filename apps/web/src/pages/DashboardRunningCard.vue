<script setup lang="ts">
import { computed } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import { currentStep, type ActiveRun } from '../control/execution';
import { t, registerMessages } from '../app/locale';
import { runPhase, runAttempt } from './execution-text';
import './dashboard-messages';

const props = defineProps<{
  items: ActiveRun[];
  bundle?: AutomationBundle;
  loading: boolean;
  error: string;
}>();
const emit = defineEmits<{ details: [] }>();
const active = computed(() =>
  props.items.filter((item) => !['completed', 'cancelled', 'failed'].includes(item.phase))
);
function script(run: ActiveRun): string {
  const step = currentStep(run);
  return props.bundle?.scripts.find((item) => item.id === step?.scriptId)?.name || step?.scriptId || '—';
}
const kind = (run: ActiveRun) => t(run.display?.targetKind === 'queue' ? '调度队列' : '脚本实例');
const tone = (run: ActiveRun) =>
  ['failed', 'reconciliationRequired'].includes(run.phase) ? 'danger' : 'info';
registerMessages({
  任务: 'Task',
  类型: 'Type',
  模式: 'Mode',
  进度: 'Progress',
  状态: 'Status',
  手动: 'Manual',
  自动: 'Automatic'
});
</script>

<template>
  <div class="running-card">
    <div class="card-head">
      <strong>{{ t('运行中') }}</strong>
      <span>{{ active.length }} {{ t('个活动任务') }}</span>
    </div>
    <p v-if="loading && !items.length" role="status">{{ t('正在读取运行状态…') }}</p>
    <p v-else-if="error" role="alert">{{ t(error) }}</p>
    <div v-else-if="!active.length" class="running-empty">
      <strong>{{ t('当前空闲') }}</strong>
      <p>
        {{ t('没有正在运行的脚本或调度队列。') }}
        <button @click="emit('details')">{{ t('前往调度中心') }}</button>
      </p>
    </div>
    <template v-else>
      <nexus-scroll-area class="running-table" direction="horizontal" :label="t('当前运行')">
        <table>
          <thead>
            <tr>
              <th v-for="label in ['任务', '类型', '模式', '进度', '状态']" :key="label" scope="col">
                {{ t(label) }}
              </th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="run in active" :key="run.runId">
              <td>
                <strong>{{ run.display?.targetName || run.runId }}</strong>
              </td>
              <td>{{ kind(run) }}</td>
              <td>{{ run.display ? t(run.display.mode === 'auto' ? '自动' : '手动') : '—' }}</td>
              <td>
                {{ script(run) }} {{ runPhase(currentStep(run)?.phase || run.phase) }}
                <br />
                <small>{{ runAttempt(run) }}</small>
              </td>
              <td><nexus-badge :tone="tone(run)" :label="runPhase(run.phase)" /></td>
            </tr>
          </tbody>
        </table>
      </nexus-scroll-area>
      <div class="running-records">
        <article v-for="run in active" :key="run.runId" class="running-record">
          <header>
            <strong>{{ run.display?.targetName || run.runId }}</strong>
            <nexus-badge :tone="tone(run)" :label="runPhase(run.phase)" />
          </header>
          <div class="record-meta">
            <span>{{ kind(run) }}</span>
            <span>{{ run.display ? t(run.display.mode === 'auto' ? '自动' : '手动') : '—' }}</span>
          </div>
          <p>
            {{ script(run) }} {{ runPhase(currentStep(run)?.phase || run.phase) }}
            <br />
            <small>{{ runAttempt(run) }}</small>
          </p>
        </article>
      </div>
    </template>
  </div>
</template>

<style scoped>
.running-card {
  display: grid;
  gap: 12px;
}
.card-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
}
.card-head > strong {
  font-size: 15px;
  line-height: 1.35;
}
.card-head > span,
small {
  color: var(--muted);
  font-size: 12px;
}
table {
  width: 100%;
  min-width: 680px;
  border-collapse: collapse;
  font-size: 13px;
}
th,
td {
  padding: 13px 12px;
  text-align: left;
  border-bottom: 1px solid var(--border);
  vertical-align: middle;
}
th {
  color: var(--muted);
  font-size: 11px;
  font-weight: 750;
  letter-spacing: 0.04em;
  white-space: nowrap;
}
td {
  line-height: 1.55;
}
.running-records {
  display: none;
}
.running-record {
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--content-card-soft);
}
.running-record header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.running-record header strong {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.record-meta {
  display: flex;
  gap: 8px;
  margin-top: 4px;
  color: var(--muted);
  font-size: 12px;
}
.running-record p {
  margin: 8px 0 0;
  font-size: 12px;
  line-height: 1.6;
}
.running-empty {
  padding: 48px 20px;
  text-align: center;
  border: 1px solid var(--border);
  border-radius: 16px;
  font-size: 13px;
}
.running-empty > strong {
  display: block;
  margin-bottom: 5px;
  font-size: 15px;
}
.running-empty p {
  margin: 0;
  color: var(--muted);
  font-size: 13px;
}
.running-empty button {
  min-height: 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--accent);
  font-weight: 400;
  line-height: inherit;
}
@media (max-width: 600px) {
  .running-table {
    display: none;
  }
  .running-records {
    display: grid;
    gap: 8px;
  }
}
</style>
