<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import { attemptTitle } from './history-format';
import { readHistoryLog } from './history-log';
import VirtualHistoryList from '../components/VirtualHistoryList.vue';
import { t } from '../app/locale';
import { historyLogId, type HistoryLog } from './attempt-resources';
const props = withDefaults(
  defineProps<{
    store: RecordsStore;
    runId: string;
    attemptId: string;
    visible: boolean;
    ordinal?: number;
    logs: HistoryLog[];
  }>(),
  { ordinal: 1 }
);
const disclosure = ref<'closed' | 'opening' | 'open'>('closed');
const trigger = ref<HTMLButtonElement>();
const content = ref<HTMLElement>();
const text = shallowRef<readonly string[]>([]);
const error = ref('');
const busy = ref(false);
const selectedId = ref('');
const title = computed(() => t('脚本日志') + '（' + attemptTitle(props.ordinal) + '）');
const selected = computed(() => props.logs.find(log => historyLogId(log) === selectedId.value) ?? props.logs[0]);
const request = computed(() => {
  const log = selected.value;
  return props.visible && disclosure.value !== 'closed' && log
    ? JSON.stringify([props.runId, props.attemptId, historyLogId(log), log.snapshotSha256]) : '';
});
let owner: AbortController | undefined;
function showLog(): void {
  const moveFocus = trigger.value === document.activeElement;
  disclosure.value = 'open';
  if (moveFocus) void nextTick(() => content.value?.focus({ preventScroll: true }));
}
function expand(): void {
  if (disclosure.value !== 'closed') return;
  if (selected.value) disclosure.value = 'opening';
  else showLog();
}
function conceal(panel: Element): void {
  (panel as HTMLElement).inert = true;
}
async function load(): Promise<void> {
  owner?.abort();
  owner = undefined;
  text.value = [];
  error.value = '';
  const log = selected.value;
  const fingerprint = request.value;
  busy.value = !!fingerprint;
  if (!fingerprint || !log) return;
  const controller = new AbortController();
  owner = controller;
  try {
    const value = await readHistoryLog(props.store.client, props.runId, props.attemptId, log, controller.signal);
    if (!controller.signal.aborted && request.value === fingerprint) text.value = value;
  } catch (reason) {
    if (!controller.signal.aborted && request.value === fingerprint)
      error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (owner === controller) {
      owner = undefined;
      busy.value = false;
      if (!controller.signal.aborted && request.value === fingerprint && disclosure.value === 'opening') showLog();
    }
  }
}
watch([() => props.runId, () => props.attemptId], () => {
  disclosure.value = 'closed';
  selectedId.value = '';
});
watch(request, load);
onBeforeUnmount(() => owner?.abort());
</script>

<template>
  <div class="history-log-disclosure">
    <button v-if="disclosure !== 'open'" ref="trigger" type="button" class="history-log-toggle"
      :aria-disabled="disclosure === 'opening'" @click="expand">
      <nexus-spinner v-if="disclosure === 'opening'" :label="t('正在读取脚本日志…')" />
      <template v-else>
        <strong>{{ title }}</strong>
        <span>{{ t('点击展开') }}</span>
      </template>
    </button>
    <Transition :key="runId + '/' + attemptId" name="disclosure" @before-leave="conceal">
      <div v-if="visible && disclosure === 'open'" class="disclosure-reveal">
        <div class="disclosure-clip">
          <div ref="content" class="history-log-content" role="region" :aria-label="title" :aria-busy="busy" tabindex="-1">
            <nav v-if="logs.length > 1" :aria-label="t('脚本日志')">
              <VirtualHistoryList :items="logs" :item-key="historyLogId" :label="t('脚本日志')" :visible="visible">
                <template #default="{ item: log }">
                  <button type="button" :aria-pressed="selected && historyLogId(selected) === historyLogId(log)"
                    @click="selectedId = historyLogId(log)">
                    {{ log.key.stepId }} · {{ t(log.key.role) }}<template v-if="log.key.invocationOrdinal">
                      · {{ attemptTitle(log.key.invocationOrdinal) }}</template>
                  </button>
                </template>
              </VirtualHistoryList>
            </nav>
            <nexus-spinner v-if="busy" :label="t('正在读取脚本日志…')" />
            <template v-else-if="error">
              <nexus-toast tone="danger" :message.prop="t(error)" />
              <button type="button" @click="load">{{ t('重试') }}</button>
            </template>
            <nexus-scroll-area v-else class="history-log-preview" direction="vertical" :label="t('运行日志')">
              <pre><template v-if="text.length"><template v-for="(segment, index) in text" :key="index">{{ segment }}</template></template><template v-else>{{ t('没有可用的日志') }}</template></pre>
            </nexus-scroll-area>
          </div>
        </div>
      </div>
    </Transition>
  </div>
</template>
<style scoped>
.history-log-disclosure {
  min-width: 0;
}
.history-log-toggle {
  box-sizing: border-box;
  width: 100%;
  min-height: 76px;
  padding: 14px;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  background: transparent;
  border: 1px dashed var(--border-strong);
  border-radius: 8px;
  cursor: pointer;
}
.history-log-toggle strong {
  font-size: 12px;
  font-weight: 400;
  color: var(--text);
}
.history-log-toggle span {
  color: var(--muted);
  font-size: 12px;
  font-weight: 400;
}
.history-log-toggle[aria-disabled='true'] {
  cursor: progress;
  opacity: 1;
}
.history-log-preview {
  max-width: 100%;
  min-height: 84px;
  max-height: 260px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--log-bg);
  color: var(--log-text, #c7e1fc);
}
.history-log-preview pre {
  margin: 0;
  padding: 12px;
  font:
    12px/1.6 Consolas,
    monospace;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
