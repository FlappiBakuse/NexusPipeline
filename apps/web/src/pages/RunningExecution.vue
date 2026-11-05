<script setup lang="ts">
import { computed } from 'vue';
import type { AppStore } from '../app/store';
import { currentStep, finishedSteps, type ActiveRun } from '../control/execution';
import LiveLogPanel from '../components/LiveLogPanel.vue';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import LiveTaskSteps from './LiveTaskSteps.vue';
import { t, registerMessages } from '../app/locale';
import { runPhase, runAttempt } from './execution-text';

const props = defineProps<{ store: AppStore; run: ActiveRun; visible: boolean; cancelling: boolean }>();
const emit = defineEmits<{ cancel: [run: ActiveRun] }>();
const current = computed(() => currentStep(props.run));
const script = computed(() =>
  props.store.state.automation?.bundle.scripts.find((item) => item.id === current.value?.scriptId)
);
const queue = computed(() => props.run.display?.targetKind === 'queue');
const progress = computed(() =>
  queue.value
    ? (finishedSteps(props.run) / Math.max(1, props.run.steps.length)) * 100
    : (props.run.ordinal / Math.max(1, props.run.display?.maximumAttempts || props.run.ordinal)) * 100
);
const identity = computed(() =>
  current.value
    ? { runId: props.run.runId, attemptId: props.run.attemptId, stepId: current.value.stepId, role: 'main' }
    : undefined
);
registerMessages({
  手动: 'Manual',
  自动: 'Automatic',
  取消运行: 'Cancel run',
  '当前：': 'Current: ',
  第: 'Attempt',
  次: '',
  项已完成: 'items completed'
});
</script>

<template>
  <article class="running-item" :data-run-id="run.runId">
    <header>
      <div class="run-title">
        <strong>{{ run.display?.targetName || run.runId }}</strong>
        <nexus-badge :tone="queue ? 'info' : 'neutral'" :label="t(queue ? '调度队列' : '脚本实例')" />
        <nexus-badge
          v-if="run.display"
          tone="neutral"
          :label="t(run.display.mode === 'auto' ? '自动' : '手动')"
        />
        <span v-if="queue" class="muted">
          {{ finishedSteps(run) }}/{{ run.steps.length }} {{ t('项已完成') }}
        </span>
      </div>
      <button
        class="danger"
        :disabled="cancelling || !run.canCancel || !store.can('execution.cancel')"
        @click="emit('cancel', run)"
      >
        {{ t('取消运行') }}
      </button>
    </header>
    <p class="current-step">
      {{ t('当前：') }}{{ script?.name || current?.scriptId || '—' }}
      {{ runPhase(run.phase) }} · {{ runAttempt(run) }}
    </p>
    <div
      class="run-progress"
      role="progressbar"
      :aria-label="t('运行进度')"
      :aria-valuenow="Math.round(progress)"
      aria-valuemin="0"
      aria-valuemax="100"
    >
      <span :style="{ width: Math.min(100, progress) + '%' }" />
    </div>
    <LiveTaskSteps v-if="current?.tasks.length" :tasks="current.tasks" />
    <div class="running-content">
      <LiveLogPanel :store="store" v-bind="identity ? { identity } : {}"
        :roles="current?.logRoles || []" :visible="visible && run.phase !== 'reconciliationRequired'" />
      <ExtensionOutlet
        :store="store"
        v-if="!cancelling && !run.cancellationRequested && ['preparing', 'running'].includes(run.phase)"
        target="execution.running.preview"
        :visible="visible"
        :run-id="run.runId"
        :attempt-id="run.attemptId"
      />
    </div>
    <ExtensionOutlet
      :store="store"
      target="execution.running.badges"
      :visible="visible"
      :run-id="run.runId"
      :attempt-id="run.attemptId"
    />
  </article>
</template>

<style scoped>
.running-item {
  padding: 14px 0;
  border-bottom: 1px solid var(--border);
}
.running-item:last-child {
  border-bottom: 0;
}
header,
.run-title {
  display: flex;
  align-items: center;
  gap: 6px;
}
header {
  align-items: flex-start;
  gap: 12px;
  justify-content: space-between;
  min-height: 40px;
}
.run-title {
  flex-wrap: wrap;
  min-width: 0;
}
.run-title strong {
  font-size: 14px;
  overflow-wrap: anywhere;
}
header button {
  flex: none;
  font-size: 12px;
  padding-inline: 11px;
}
.current-step {
  margin: 9px 0 0;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.55;
}
.muted {
  color: var(--muted);
  font-size: 12px;
}
.run-progress {
  height: 7px;
  margin-top: 10px;
  border-radius: 6px;
  overflow: hidden;
  background: var(--content-card-soft);
}
.run-progress > span {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: var(--accent-strong);
  transition: width 0.4s ease;
}
.running-content:has(.surface-outlet-content:not(:empty)) {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(280px, 1fr);
  gap: 16px;
  align-items: stretch;
  margin-top: 16px;
}
.running-content:has(.surface-outlet-content:not(:empty)) :deep(.run-log-resizable) {
  margin-top: 0;
}
@media (max-width: 1100px) {
  .running-content:has(.surface-outlet-content:not(:empty)) {
    grid-template-columns: minmax(0, 1fr);
  }
}
@media (prefers-reduced-motion: reduce) {
  .run-progress > span {
    transition: none;
  }
}
</style>
