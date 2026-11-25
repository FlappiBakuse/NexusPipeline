<script setup lang="ts">
import { computed } from 'vue';
import type { AppStore } from '../app/store';
import { currentStep, finishedSteps, type ActiveRun } from '../control/execution';
import type { LiveLogs } from '../control/live-logs';
import LiveLogPanel from '../components/LiveLogPanel.vue';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import LiveTaskSteps from './LiveTaskSteps.vue';
import { t, registerMessages } from '../app/locale';
import { runPhase, runAttempt, runPhaseTone, runProgress, runScriptName, runTitle } from './execution-text';

const props = defineProps<{ store: AppStore; run: ActiveRun; visible: boolean; cancelling: boolean; cancelBlocked: boolean }>();
const emit = defineEmits<{ cancel: [run: ActiveRun] }>();
const current = computed(() => currentStep(props.run));
const bundle = computed(() => props.store.state.automation?.bundle);
const queue = computed(() => props.run.display?.targetKind === 'queue');
const progress = computed(() => runProgress(props.run));
const stopping = computed(() => props.cancelling || props.run.cancellationRequested || props.run.phase === 'stopping');
function stepLabel(scriptId: string, userId: string | null): string {
  const script = bundle.value?.scripts.find(item => item.id === scriptId)?.name || scriptId;
  const user = bundle.value?.users.find(item => item.id === userId)?.name;
  return user ? script + ' · ' + user : script;
}
const identity = computed(() =>
  current.value
    ? { runId: props.run.runId, attemptId: props.run.attemptId, stepId: current.value.stepId }
    : undefined
);
const subscribeLog: LiveLogs['subscribe'] = (key, listener) => props.store.logs.subscribe(key, listener);
registerMessages({
  手动: 'Manual',
  自动: 'Automatic',
  取消运行: 'Cancel run',
  '当前：': 'Current: ',
  第: 'Attempt',
  次: '',
  队列步骤进度: 'Queue step progress',
  '正在停止…': 'Stopping…'
});
</script>

<template>
  <article class="running-item" :data-run-id="run.runId">
    <header>
      <div class="run-title">
        <span class="phase-dot" :data-tone="runPhaseTone(run.phase)" aria-hidden="true"></span>
        <strong>{{ runTitle(run) }}</strong>
        <nexus-badge :tone="queue ? 'info' : 'neutral'" :label="t(queue ? '调度队列' : '脚本实例')" />
        <nexus-badge
          v-if="run.display"
          tone="neutral"
          :label="t(run.display.mode === 'auto' ? '自动' : '手动')"
        />
        <nexus-badge :tone="runPhaseTone(run.phase)" :label="runPhase(run.phase)" />
      </div>
      <button
        v-if="run.phase !== 'reconciliationRequired'"
        class="danger"
        type="button"
        :disabled="cancelBlocked || stopping || !run.canCancel || !store.can('execution.cancel')"
        @click="emit('cancel', run)"
      >
        {{ t(stopping ? '正在停止…' : '取消运行') }}
      </button>
    </header>
    <p class="current-step">
      {{ t('当前：') }}{{ runScriptName(run, bundle?.scripts) }} · {{ runAttempt(run) }}
    </p>
    <div
      class="run-progress"
      role="progressbar"
      :aria-label="t('运行进度')"
      :aria-valuenow="progress"
      :data-tone="runPhaseTone(run.phase)"
      aria-valuemin="0"
      aria-valuemax="100"
    >
      <span :style="{ width: progress + '%' }" />
    </div>
    <section v-if="queue && run.steps.length" class="run-steps">
      <strong>{{ t('队列步骤进度') }} ({{ finishedSteps(run) }}/{{ run.steps.length }})</strong>
      <nexus-scroll-area direction="horizontal" :label="t('队列步骤进度')">
        <ol>
          <li v-for="(step, index) in run.steps" :key="step.stepId"
            :data-phase="step.phase" :aria-current="step === current && !['restored', 'skipped', 'notExecuted', 'pending'].includes(step.phase) ? 'step' : undefined">
            <span>{{ index + 1 }}. {{ stepLabel(step.scriptId, step.userId) }}</span>
            <small>{{ runPhase(step.phase === 'restored' ? 'completed' : step.phase) }}</small>
          </li>
        </ol>
      </nexus-scroll-area>
    </section>
    <LiveTaskSteps v-if="current?.tasks.length" :tasks="current.tasks" />
    <div v-if="run.phase !== 'reconciliationRequired'" class="running-content">
      <LiveLogPanel :key="(current?.stepId || '') + '/' + (current?.invocationOrdinal || 0)"
        :subscribe="subscribeLog" v-bind="identity ? { identity } : {}"
        :roles="current?.logRoles || []" :visible="visible" />
      <ExtensionOutlet
        :store="store"
        v-if="!stopping && ['preparing', 'running'].includes(run.phase)"
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
  min-width: 0;
  container: run-detail / inline-size;
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
  min-height: 30px;
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
.phase-dot {
  flex: none;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--accent);
  box-shadow: 0 0 0 3px var(--accent-soft);
}
.phase-dot[data-tone='warning'],
.run-progress[data-tone='warning'] > span { background: var(--warn); }
.phase-dot[data-tone='danger'] { background: var(--bad); }
.run-steps {
  display: grid;
  gap: 4px;
  margin-top: 12px;
  font-size: 12px;
}
.run-steps ol {
  display: flex;
  gap: 6px;
  margin: 0;
  padding: 2px 0 6px;
  list-style: none;
}
.run-steps li {
  display: flex;
  align-items: center;
  gap: 6px;
  flex: none;
  padding: 4px 8px;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: var(--content-card-soft);
}
.run-steps li[aria-current='step'] {
  border-color: var(--accent);
  background: var(--accent-soft);
}
.run-steps li[data-phase='restored'] small { color: var(--ok); }
.run-steps small { color: var(--muted); }
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
  grid-template-columns: minmax(0, 1.65fr) minmax(195px, 1fr);
  gap: 10px;
  align-items: stretch;
  margin-top: 16px;
}
.running-content:has(.surface-outlet-content:not(:empty)) :deep(.run-log-resizable) {
  margin-top: 0;
}
@container run-detail (max-width: 520px) {
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
