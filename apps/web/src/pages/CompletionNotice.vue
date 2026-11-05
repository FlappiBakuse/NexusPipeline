<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { objectValue, stringValue } from '../control/client';
import { CompletionCommands } from './completion-commands';
import { t } from '../app/locale';
const props = defineProps<{ store: AppStore; visible: boolean }>();
const instance = props.store.state.capabilities?.instanceId;
type Completion = {
  operationId: string | null;
  action: string | null;
  phase: string | null;
  notBeforeUtc: string | null;
  serverTimeUtc: string;
  canCancel: boolean;
  failureCode: string | null;
};
const completion = ref<Completion | null>(null);
const completionError = ref('');
const commands = new CompletionCommands(props.store);
const cancellationPending = computed(() => commands.state.pending);
const cancelling = computed(() => commands.state.busy);
const now = ref(Date.now());
const clockOffset = ref(0);
const secondsLeft = computed(() =>
  completion.value?.notBeforeUtc
    ? Math.max(
        0,
        Math.ceil((Date.parse(completion.value.notBeforeUtc) - now.value - clockOffset.value) / 1000)
      )
    : 0
);
const actionLabels: Record<string, string> = {
  Shutdown: '关机',
  Hibernate: '休眠',
  Restart: '重启',
  Exit: '退出 Host'
};
const actionName = computed(
  () => actionLabels[completion.value?.action || ''] || completion.value?.action || '系统操作'
);
const phaseLabels: Record<string, string> = {
  Prepared: '准备中',
  Countdown: '倒计时',
  Entered: '已进入',
  Accepted: '已受理',
  Cancelled: '已取消',
  Failed: '失败',
  Unknown: '结果未知'
};
const phaseName = computed(
  () => phaseLabels[completion.value?.phase || ''] || completion.value?.phase || '待处理'
);
let completionOwner: AbortController | undefined;
async function readCompletion(): Promise<boolean> {
  completionOwner?.abort();
  if (
    !props.visible ||
    instance !== props.store.state.capabilities?.instanceId ||
    !props.store.can('control.status.read')
  )
    return false;
  const owner = new AbortController();
  completionOwner = owner;
  try {
    const row = await props.store.client.read('/control/v1/completion/pending', objectValue, owner.signal);
    owner.signal.throwIfAborted();
    if (instance !== props.store.state.capabilities?.instanceId) return false;
    clockOffset.value = Date.parse(stringValue(row.serverTimeUtc)) - Date.now();
    completion.value = {
      operationId: row.operationId == null ? null : stringValue(row.operationId),
      action: row.action == null ? null : stringValue(row.action),
      phase: row.phase == null ? null : stringValue(row.phase),
      notBeforeUtc: row.notBeforeUtc == null ? null : stringValue(row.notBeforeUtc),
      serverTimeUtc: stringValue(row.serverTimeUtc),
      canCancel: row.canCancel === true,
      failureCode: row.failureCode == null ? null : stringValue(row.failureCode)
    };
    completionError.value = '';
    commands.observe(completion.value.operationId, completion.value.phase);
    return true;
  } catch (reason) {
    if (!owner.signal.aborted) {
      props.store.report(reason);
      completionError.value = reason instanceof Error ? reason.message : '系统操作状态不可用。';
    }
    return false;
  }
}
async function cancelCompletion(): Promise<void> {
  const id = completion.value?.operationId;
  if (
    !id ||
    cancelling.value ||
    cancellationPending.value ||
    !completion.value?.canCancel ||
    !props.store.can('completion.local')
  )
    return;
  if (await commands.cancel(id)) await checkCompletion();
  else
    completionError.value = cancellationPending.value
      ? '取消结果待确认；请读取 Host 状态，勿重复发送。'
      : '取消失败。';
}
async function checkCompletion(): Promise<void> {
  await readCompletion();
}

let clock: ReturnType<typeof setInterval> | undefined;
let polling: ReturnType<typeof setInterval> | undefined;
watch(
  () => props.visible,
  (visible) => {
    clearInterval(clock);
    clearInterval(polling);
    completionOwner?.abort();
    if (!visible) return;
    now.value = Date.now();
    void readCompletion();
    clock = setInterval(() => {
      now.value = Date.now();
    }, 1000);
    polling = setInterval(() => {
      void readCompletion();
    }, 5000);
  },
  { immediate: true }
);
onBeforeUnmount(() => {
  clearInterval(clock);
  clearInterval(polling);
  completionOwner?.abort();
});
</script>
<template>
  <section v-if="completion?.operationId" class="completion" role="status" aria-live="polite">
    <h2>{{ t('待执行系统操作') }}</h2>
    <p>
      {{ t(actionName) }} · {{ t(phaseName) }}
      <template v-if="completion.notBeforeUtc">· {{ secondsLeft }} {{ t('秒后执行') }}</template>
    </p>
    <p v-if="completion.failureCode">{{ completion.failureCode }}</p>
    <button
      v-if="completion.canCancel && store.can('completion.local')"
      :disabled="cancelling || !!cancellationPending"
      @click="cancelCompletion"
    >
      {{ t('取消系统操作') }}
    </button>
    <button v-if="cancellationPending" @click="checkCompletion">{{ t('读取状态确认取消结果') }}</button>
  </section>
  <p v-if="completionError" role="alert">{{ t(completionError) }}</p>
</template>
<style scoped src="./completion-notice.css"></style>
