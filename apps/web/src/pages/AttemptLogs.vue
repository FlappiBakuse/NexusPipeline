<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import { attemptTitle } from './history-detail';
import { readHistoryLog } from './history-log';
import HistoryCatalogWindow from '../components/HistoryCatalogWindow.vue';
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
const expanded = ref(false);
const text = ref('');
const error = ref('');
const busy = ref(false);
const selectedId = ref('');
const lines = computed(() => {
  const value = text.value;
  if (!value) return 0;
  let count = 1;
  for (let i = value.indexOf('\n'); i !== -1; i = value.indexOf('\n', i + 1)) count++;
  return count;
});
const title = computed(() => t('脚本日志') + '（' + attemptTitle(props.ordinal) + '）');
const selected = computed(() => props.logs.find(log => historyLogId(log) === selectedId.value) ?? props.logs[0]);
const request = computed(() => {
  const log = selected.value;
  return props.visible && expanded.value && log
    ? JSON.stringify([props.runId, props.attemptId, historyLogId(log), log.snapshotSha256]) : '';
});
let owner: AbortController | undefined;
async function load(): Promise<void> {
  owner?.abort();
  owner = undefined;
  text.value = error.value = '';
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
    }
  }
}
function toggle(event: Event): void {
  expanded.value = (event.currentTarget as HTMLDetailsElement).open;
}
watch([() => props.runId, () => props.attemptId], () => {
  expanded.value = false;
  selectedId.value = '';
});
watch(request, load);
onBeforeUnmount(() => owner?.abort());
</script>

<template>
  <details class="history-log-disclosure" :open="expanded" @toggle="toggle">
    <summary class="history-log-toggle">
      <strong>{{ title }}</strong>
      <span v-if="!expanded">{{ t('点击展开') }}</span>
      <span v-else-if="visible && !busy && !error && selected">{{ lines }} {{ t('行脚本日志') }}</span>
    </summary>
    <div v-if="visible && expanded" class="history-log-content" :aria-busy="busy">
      <nav v-if="logs.length > 1" :aria-label="t('脚本日志')">
        <HistoryCatalogWindow :items="logs" :item-key="historyLogId" :label="t('脚本日志')" :visible="visible">
          <template #default="{ item: log }">
            <button :aria-pressed="selected && historyLogId(selected) === historyLogId(log)" @click="selectedId = historyLogId(log)">
              {{ log.key.stepId }} · {{ t(log.key.role) }}<template v-if="log.key.invocationOrdinal">
                · {{ attemptTitle(log.key.invocationOrdinal) }}</template>
            </button>
          </template>
        </HistoryCatalogWindow>
      </nav>
      <nexus-spinner v-if="busy" :label="t('正在读取脚本日志…')" />
      <template v-else-if="error">
        <nexus-toast tone="danger" :message.prop="t(error)" />
        <button type="button" @click="load">{{ t('重试') }}</button>
      </template>
      <nexus-scroll-area v-else class="history-log-preview" direction="vertical" :label="t('运行日志')">
        <pre>{{ text || t('没有可用的日志') }}</pre>
      </nexus-scroll-area>
    </div>
  </details>
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
.history-log-content {
  margin-top: 12px;
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
