<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import { arrayValue, objectValue, stringValue } from '../control/client';
import { attemptTitle } from './history-detail';
import HistoryLogViewer from './HistoryLogViewer.vue';
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
const full = ref(false);
const text = ref('');
const omitted = ref(false);
const error = ref('');
const busy = ref(false);
const selectedId = ref('');
const lines = computed(() => (text.value ? text.value.split('\n').length : 0));
const preview = computed(() => text.value.split('\n').slice(-200).join('\n'));
const title = computed(() => t('脚本日志') + '（' + attemptTitle(props.ordinal) + '）');
const selected = computed(() => props.logs.find(log => historyLogId(log) === selectedId.value));
let owner: AbortController | undefined;
let loadedKey = '', requestKey = '';
function decode(value: unknown): string {
  const encoded = stringValue(value);
  const bytes = Uint8Array.from(atob(encoded), (character) => character.charCodeAt(0));
  return new TextDecoder('utf-8').decode(bytes);
}
async function load(): Promise<void> {
  owner?.abort();
  const log = selected.value;
  if (!props.visible || !expanded.value || !log) return;
  const controller = new AbortController();
  owner = controller;
  const key = log.key;
  const fingerprint = JSON.stringify([props.runId, props.attemptId, historyLogId(log), log.snapshotSha256]);
  requestKey = fingerprint;
  busy.value = true;
  error.value = '';
  loadedKey = '';
  text.value = '';
  omitted.value = false;
  try {
    const base = '/control/v1/records/' + props.runId + '/attempts/' + props.attemptId + '/logs/' +
      encodeURIComponent(key.stepId) + '/' + encodeURIComponent(key.role);
    const tail = await props.store.client.read(
      base + '/tail?maximumBytes=65536' + (key.invocationOrdinal ? '&invocationOrdinal=' + key.invocationOrdinal : ''),
      objectValue, controller.signal);
    controller.signal.throwIfAborted();
    const received = objectValue(objectValue(tail.description).key);
    if (received.runId !== props.runId || received.attemptId !== props.attemptId ||
        received.stepId !== key.stepId || received.role !== key.role ||
        (received.invocationOrdinal ?? null) !== (key.invocationOrdinal ?? null))
      throw Error('history_log_identity_invalid');
    text.value = arrayValue(tail.slices, item => decode(objectValue(item).bytes)).join('');
    omitted.value = tail.prefixOmitted === true;
    loadedKey = fingerprint;
  } catch (reason) {
    if (!controller.signal.aborted) error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (owner === controller) { busy.value = false; requestKey = ''; }
  }
}
watch(
  () => [props.runId, props.attemptId] as const,
  () => {
    owner?.abort();
    text.value = '';
    omitted.value = false;
    expanded.value = false;
    full.value = false;
    selectedId.value = '';
    loadedKey = ''; requestKey = '';
  }
);
watch(() => [props.visible, expanded.value, selectedId.value,
  JSON.stringify(props.logs.map(log => [historyLogId(log), log.snapshotSha256]))] as const, () => {
  if (!props.visible || !expanded.value) {
    owner?.abort(); owner = undefined; requestKey = ''; busy.value = false;
    if (!props.visible) full.value = false;
    return;
  }
  if (!selected.value && props.logs[0]) { selectedId.value = historyLogId(props.logs[0]); return; }
  if (!selected.value) { text.value = ''; omitted.value = false; loadedKey = ''; return; }
  const fingerprint = JSON.stringify([props.runId, props.attemptId, historyLogId(selected.value), selected.value.snapshotSha256]);
  if (loadedKey !== fingerprint && requestKey !== fingerprint) void load();
}, { immediate: true });
onBeforeUnmount(() => owner?.abort());
</script>

<template>
  <section class="history-log-disclosure" :aria-busy="busy">
    <p v-if="error" role="alert">
      {{ t(error) }}
      <button @click="load">{{ t('重试') }}</button>
    </p>
    <button v-if="!expanded" class="history-log-toggle" :aria-expanded="false" @click="expanded = true">
      <strong>{{ title }}</strong>
      <span>{{ t('点击展开') }}</span>
    </button>
    <template v-else>
      <p class="history-log-label">
        {{ title }}
        <span v-if="!busy && !error && selected && !omitted">· {{ lines }} {{ t('行脚本日志') }}</span>
      </p>
      <nav v-if="logs.length > 1" :aria-label="t('脚本日志')">
        <HistoryCatalogWindow :items="logs" :item-key="historyLogId" :label="t('脚本日志')" :visible="visible">
          <template #default="{ item: log }">
            <button :aria-pressed="selectedId === historyLogId(log)" @click="selectedId = historyLogId(log)">
              {{ log.key.stepId }} · {{ t(log.key.role) }}<template v-if="log.key.invocationOrdinal">
                · {{ attemptTitle(log.key.invocationOrdinal) }}</template>
            </button>
          </template>
        </HistoryCatalogWindow>
      </nav>
      <nexus-scroll-area class="history-log-preview" direction="both" :label="t('运行日志')">
        <pre>{{ busy ? '' : preview || t('没有可用的日志') }}</pre>
      </nexus-scroll-area>
      <button v-if="selected" class="history-log-full" @click="full = true">
        {{ t('查看完整日志') }}
      </button>
    </template>
    <HistoryLogViewer
      v-if="full"
      :store="store"
      :run-id="runId"
      :attempt-id="attemptId"
      :visible="visible"
      :logs="logs"
      :initial-log="selected"
      @close="full = false"
    />
  </section>
</template>
<style scoped>
.history-log-toggle {
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
}
.history-log-toggle strong {
  font-size: 12px;
  font-weight: 400;
  color: var(--text);
}
.history-log-toggle span,
.history-log-label {
  color: var(--muted);
  font-size: 12px;
  font-weight: 400;
}
.history-log-label {
  margin: 0 0 12px;
}
.history-log-preview {
  min-height: 84px;
  max-height: 260px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--log-background, #0a1321);
  color: var(--log-text, #c7e1fc);
}
.history-log-preview pre {
  margin: 0;
  padding: 12px;
  font:
    12px/1.6 Consolas,
    monospace;
  white-space: pre;
}
.history-log-full {
  margin-top: 12px;
}
</style>
