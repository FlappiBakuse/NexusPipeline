<script setup lang="ts">
import { computed, ref, watch, onBeforeUnmount } from 'vue';
import type { AppStore } from '../app/store';
import { objectValue, arrayValue, ControlFailure } from '../control/client';
import { recordRow } from '../control/records';
import { t, registerMessages } from '../app/locale';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import RunningExecution from './RunningExecution.vue';
import { executionPreview, executionRun, type ExecutionPreview, type ActiveRun } from '../control/execution';
import { ExecutionCommands } from './execution-commands';
import ExecutionPlan from './ExecutionPlan.vue';
const props = defineProps<{ store: AppStore; visible: boolean }>();
const targetKind = ref<'queue' | 'script' | 'binding' | 'projectInitialization'>('script');
const targetId = ref('');
const preview = ref<ExecutionPreview>();
const planOpen = ref(false);
const busy = ref(false);
const commands = new ExecutionCommands(props.store);
const pending = computed(() => commands.state.pending);
const confirmation = ref<ActiveRun>();
const initializationConfirmation = ref('');
const active = ref<ActiveRun[]>([]);
const targetHint = ref('');
let hintTimer: ReturnType<typeof setTimeout> | undefined;
registerMessages({
  项目初始化: 'Project initialization',
  执行初始化: 'Run initialization',
  '仅执行此绑定已保存的初始化任务，不运行游戏任务。初始化可能修改脚本环境，确定执行吗？':
    'Run only the saved initialization tasks for this binding. This may modify the script environment. Continue?',
  请选择脚本实例: 'Choose a script instance',
  请选择调度队列: 'Choose a dispatch queue',
  运行目标: 'Run target',
  脚本组队列: 'Script group queue',
  账号绑定: 'Account binding',
  选择目标: 'Choose a target',
  预览计划: 'Preview plan',
  开始运行: 'Start run',
  当前运行: 'Current run',
  请求取消: 'Request stop',
  活动运行与任务树: 'Active runs and task tree',
  任务报告: 'Task report',
  计划详情: 'Plan details',
  暂无当前运行: 'No active run',
  '选择目标，预览后开始运行。': 'Select a target and preview its plan before starting.',
  '请求停止此运行？配置恢复可能仍需要时间。':
    'Request stopping this run? Configuration restoration may still take time.',
  '运行请求已受理，正在等待宿主结果。': 'Run accepted. Waiting for Host results.',
  '已提交停止请求；请等待宿主确认停止与配置恢复。':
    'Stop requested. Waiting for the Host to confirm stopping and restoring configuration.',
  '写入结果待确认，请先核实原操作。':
    'The write outcome is unknown. Check the original operation before continuing.',
  查询操作状态: 'Check operation',
  日志来源: 'Log source',
  主任务: 'Main task',
  前置操作: 'Pre-hook',
  后置操作: 'Post-hook',
  '最终证据待运行记录提交后可用。': 'Final evidence is available after the run record is committed.',
  检查最终证据: 'Check final evidence',
  '最终证据读取失败。': 'Final evidence is unavailable.'
});
watch(targetKind, () => {
  targetId.value = '';
  initializationConfirmation.value = '';
});
watch(
  [
    targetId,
    () => props.store.state.automation?.catalogRevision,
    () => props.store.state.settings?.saved.revision
  ],
  () => {
    preview.value = undefined;
  }
);
const options = computed(() => {
  const bundle = props.store.state.automation?.bundle;
  if (!bundle) return [];
  if (targetKind.value === 'binding' || targetKind.value === 'projectInitialization')
    return bundle.bindings.map((binding) => ({
      id: binding.id,
      name:
        (bundle.users.find((user) => user.id === binding.userId)?.name || '') +
        ' · ' +
        (bundle.scripts.find((script) => script.id === binding.scriptId)?.name || '')
    }));
  return bundle[targetKind.value === 'queue' ? 'queues' : 'scripts'];
});
function selection(): Record<string, unknown> {
  return {
    format: 'nexus.control.execution-selection',
    version: 1,
    expectedCatalogRevision: props.store.state.automation?.catalogRevision,
    expectedSettingsRevision: props.store.state.settings?.saved.revision,
    target: { kind: targetKind.value, [targetKind.value === 'projectInitialization' ? 'bindingId' : targetKind.value + 'Id']: targetId.value }
  };
}
function hasTarget(): boolean {
  if (targetId.value) return true;
  targetHint.value = t(targetKind.value === 'queue' ? '请选择调度队列' : '请选择脚本实例');
  clearTimeout(hintTimer);
  hintTimer = setTimeout(() => {
    targetHint.value = '';
  }, 3000);
  return false;
}
async function explain(): Promise<void> {
  if (busy.value || !hasTarget()) return;
  busy.value = true;
  const identity = JSON.stringify(selection());
  try {
    const result = await props.store.client.write('/control/v1/execution/preview', selection(), executionPreview);
    if (props.visible && identity === JSON.stringify(selection())) {
      preview.value = result;
      planOpen.value = true;
    }
  } catch (error) {
    props.store.report(error);
  } finally {
    busy.value = false;
  }
}
async function runSelected(confirmedInitialization = false): Promise<void> {
  if (busy.value || pending.value || !hasTarget()) return;
  const selected = selection();
  if (confirmedInitialization && initializationConfirmation.value !== JSON.stringify(selected)) return;
  if (!preview.value) await explain();
  if (
    !props.visible ||
    JSON.stringify(selected) !== JSON.stringify(selection()) ||
    preview.value?.hasRunnable !== true
  )
    return;
  planOpen.value = false;
  if (targetKind.value === 'projectInitialization' && confirmedInitialization !== true) {
    initializationConfirmation.value = JSON.stringify(selected);
    return;
  }
  initializationConfirmation.value = '';
  if (await commands.start(selected)) props.store.state.notice = t('运行请求已受理，正在等待宿主结果。');
}
async function cancel(): Promise<void> {
  const target = confirmation.value;
  if (!target || pending.value) return;
  if (await commands.cancel(target)) {
    confirmation.value = undefined;
    props.store.state.notice = t('已提交停止请求；请等待宿主确认停止与配置恢复。');
  }
}
async function checkOperation(): Promise<void> {
  if (await commands.inspect()) confirmation.value = undefined;
}
let activeOwner: AbortController | undefined;
watch(
  [() => props.store.state.revision, () => props.visible],
  async () => {
    activeOwner?.abort();
    if (!props.visible) {
      clearTimeout(hintTimer);
      targetHint.value = '';
      return;
    }
    const controller = new AbortController();
    activeOwner = controller;
    try {
      const value = await props.store.client.read(
        '/control/v1/execution/active',
        objectValue,
        controller.signal
      );
      controller.signal.throwIfAborted();
      active.value = arrayValue(value.items, executionRun);
      const runId = commands.state.acceptedRunId;
      if (runId && !active.value.some(run => run.runId === runId) && props.store.can('records.read')) {
        try {
          await props.store.client.read('/control/v1/records/' + encodeURIComponent(runId),
            result => recordRow(result, runId), controller.signal);
          controller.signal.throwIfAborted();
          if (commands.state.acceptedRunId === runId) {
            commands.state.acceptedRunId = undefined;
            if (props.store.state.notice === t('运行请求已受理，正在等待宿主结果。')) props.store.state.notice = '';
          }
        } catch (reason) {
          if (!(reason instanceof ControlFailure && reason.status === 404)) throw reason;
        }
      }
    } catch (error) {
      if (!controller.signal.aborted) props.store.report(error);
    }
  },
  { immediate: true }
);
onBeforeUnmount(() => {
  activeOwner?.abort();
  clearTimeout(hintTimer);
});
</script>
<template>
  <section class="dispatch-targets">
    <div class="toolbar target-controls">
      <label>
        {{ t('目标类型') }}
        <FormSelect v-model="targetKind" :aria-label="t('目标类型')">
          <option value="script">{{ t('脚本实例') }}</option>
          <option value="queue">{{ t('调度队列') }}</option>
          <option value="projectInitialization">{{ t('项目初始化') }}</option>
        </FormSelect>
      </label>
      <label>
        {{ t(targetKind === 'queue' ? '调度队列' : '脚本实例') }}
        <FormSelect v-model="targetId" :aria-label="t(targetKind === 'queue' ? '调度队列' : '脚本实例')">
          <option value="">{{ t(targetKind === 'queue' ? '（选择调度队列）' : '（选择脚本实例）') }}</option>
          <option v-for="item in options" :value="item.id">{{ item.name }}</option>
        </FormSelect>
      </label>
      <nexus-button
        :label="t('检查运行计划')"
        :disabled.prop="busy || !store.can('execution.preview')"
        @click="explain"
      />
      <nexus-button
        :label="t(targetKind === 'projectInitialization' ? '执行初始化' : targetKind === 'queue' ? '执行队列' : '执行脚本')"
        tone="info"
        :disabled.prop="busy || !!pending || !store.can('execution.start')"
        @click="runSelected()"
      />
    </div>
    <p v-if="pending && !commands.state.sending" role="alert">
      {{ t('写入结果待确认，请先核实原操作。') }}
      <button @click="checkOperation">{{ t('查询操作状态') }}</button>
    </p>
  </section>
  <nexus-toast
    v-if="visible && targetHint"
    class="dispatch-hint"
    :message.prop="targetHint"
    tone="danger"
    @nexus-close="targetHint = ''"
  />
  <nexus-modal
    :open.prop="visible && planOpen"
    :title="t('运行计划检查')"
    size="wide"
    @nexus-close="planOpen = false"
  >
    <ExecutionPlan
      v-if="preview"
      :preview="preview"
      :bundle="store.state.automation?.bundle"
      :target-name="options.find((item) => item.id === targetId)?.name || ''"
      :target-kind="targetKind"
      :target-id="targetId"
    />
  </nexus-modal>
  <ExtensionOutlet :store="store" target="execution.cards" :visible="visible" />
  <nexus-confirm-dialog
    :open.prop="visible && !!initializationConfirmation"
    :title="t('项目初始化')"
    :message="t('仅执行此绑定已保存的初始化任务，不运行游戏任务。初始化可能修改脚本环境，确定执行吗？')"
    :confirm-label="t('执行初始化')"
    :busy.prop="commands.state.sending"
    @nexus-confirm="runSelected(true)"
    @nexus-cancel="initializationConfirmation = ''"
    @nexus-close="initializationConfirmation = ''"
  />
  <section class="dispatch-running">
    <div class="running-heading">
      <strong>{{ t('运行中') }} ({{ active.length }})</strong>
      <small>{{ t('每 1 秒更新') }}</small>
    </div>
    <RunningExecution
      v-for="run in active"
      :key="run.runId"
      :store="store"
      :run="run"
      :visible="visible"
      :cancelling="!!pending"
      @cancel="confirmation = $event"
    />
    <nexus-empty-state
      v-if="!active.length"
      class="running-empty"
      :title="t('当前没有正在运行的任务')"
      :description="t('选择脚本或队列后，可以在这里查看实时状态。')"
    />
  </section>
  <nexus-confirm-dialog
    v-if="confirmation"
    :open.prop="visible"
    :title="t('取消运行')"
    :message="t('当前任务将被终止；如果这是调度队列，后续任务也不会继续执行。确定取消吗？')"
    :confirm-label="t('取消运行')"
    :busy.prop="commands.state.sending"
    tone="danger"
    @nexus-confirm="cancel"
    @nexus-cancel="confirmation = undefined"
    @nexus-close="confirmation = undefined"
  />
</template>
<style scoped src="./dispatch-page.css"></style>
