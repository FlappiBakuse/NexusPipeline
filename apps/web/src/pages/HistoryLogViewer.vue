<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import { arrayValue, boundedBytes, objectValue, stringValue } from '../control/client';
import { BlobDownloads } from '../control/blob-downloads';
import { decodeLogPart, neighborLogPart, trailingLogBytes } from './log-part-text';
import { t } from '../app/locale';
import { attemptTitle } from './history-detail';
import { historyLogId, type HistoryLog } from './attempt-resources';
import HistoryCatalogWindow from '../components/HistoryCatalogWindow.vue';
const props = defineProps<{
  store: RecordsStore;
  runId: string;
  attemptId: string;
  visible: boolean;
  logs: HistoryLog[];
  initialLog?: HistoryLog;
}>();
const emit = defineEmits<{ close: [] }>();
const downloads = new BlobDownloads();
const selected = ref<HistoryLog>();
const description = ref<Record<string, unknown>>();
const logText = ref('');
const logDownloadBusy = ref(false);
const partIndex = ref(0);
const follow = ref(true);
const base = computed(() => '/control/v1/records/' + props.runId + '/attempts/' + props.attemptId);
const logBase = computed(() => {
  const key = selected.value?.key as Record<string, unknown> | undefined;
  return key
    ? base.value +
        '/logs/' +
        encodeURIComponent(String(key.stepId)) +
        '/' +
        encodeURIComponent(String(key.role))
    : '';
});
function invocationQuery(prefix: string): string {
  const key = selected.value?.key as { invocationOrdinal?: number } | undefined;
  return key?.invocationOrdinal ? prefix + 'invocationOrdinal=' + key.invocationOrdinal : '';
}
let logOwner: AbortController | undefined;
let downloadOwner: AbortController | undefined;
const tails = new Map<number, Uint8Array>();
type SaveSink = WritableStream<Uint8Array> & { abort(reason?: unknown): Promise<void> };
type SaveHandle = { createWritable(): Promise<SaveSink> };
type SavePicker = (options: { suggestedName: string }) => Promise<SaveHandle>;
const savePicker = (window as Window & { showSaveFilePicker?: SavePicker }).showSaveFilePicker;
const parts = computed(() => arrayValue(description.value?.parts || [], objectValue));
const partMeta = computed(() =>
  parts.value.map((part) => ({
    index: Number(part.index),
    source: String(part.source),
    fileSourceId: part.fileSourceId == null ? null : Number(part.fileSourceId),
    fileOffset: part.fileOffset == null ? null : Number(part.fileOffset),
    sizeBytes: Number(part.sizeBytes)
  }))
);
function rememberTail(index: number, bytes: Uint8Array): void {
  tails.delete(index);
  tails.set(index, bytes.slice(-3));
  if (tails.size > 128) tails.delete(tails.keys().next().value!);
}
async function selectLog(log: HistoryLog): Promise<void> {
  logOwner?.abort();
  downloadOwner?.abort();
  const controller = new AbortController();
  logOwner = controller;
  selected.value = log;
  tails.clear();
  logText.value = '';
  description.value = undefined;
  partIndex.value = 0;
  try {
    const value = await props.store.client.read(
      logBase.value + '/description' + invocationQuery('?'),
      objectValue,
      controller.signal
    );
    controller.signal.throwIfAborted();
    const key = objectValue(value.key), requested = log.key;
    if (key.runId !== props.runId || key.attemptId !== props.attemptId || key.stepId !== requested.stepId ||
        key.role !== requested.role || (key.invocationOrdinal ?? null) !== (requested.invocationOrdinal ?? null) ||
        !/^[a-f0-9]{64}$/.test(stringValue(value.snapshotSha256))) throw Error('history_log_identity_invalid');
    description.value = value;
    await readPart(0);
  } catch (error) {
    if (!controller.signal.aborted) props.store.report(error);
  }
}
async function readPart(index: number): Promise<void> {
  if (index < 0 || index >= parts.value.length || !description.value) return;
  logOwner?.abort();
  const controller = new AbortController();
  logOwner = controller;
  try {
    const query = '?snapshotSha256=' + encodeURIComponent(String(description.value.snapshotSha256)) + invocationQuery('&');
    const previous = neighborLogPart(partMeta.value, index, -1);
    let prefix: Uint8Array = new Uint8Array();
    if (previous) {
      const cached = tails.get(previous.index);
      if (cached) prefix = cached;
      else {
        const response = await props.store.client.response(
          logBase.value + '/parts/' + previous.index + query,
          controller.signal
        );
        prefix = await trailingLogBytes(response, 1024 * 1024, controller.signal);
        controller.signal.throwIfAborted();
        rememberTail(previous.index, prefix);
      }
    }
    const response = await props.store.client.response(
      logBase.value + '/parts/' + parts.value[index].index + query,
      controller.signal
    );
    const bytes = await boundedBytes(response, 1024 * 1024);
    controller.signal.throwIfAborted();
    rememberTail(Number(parts.value[index].index), bytes);
    logText.value = decodeLogPart(bytes, prefix, !!neighborLogPart(partMeta.value, index, 1));
    partIndex.value = index;
    if (follow.value) {
      await nextTick();
      document.querySelector('.attempt-detail .log-window')?.scrollTo(0, 99999999);
    }
  } catch (error) {
    if (!controller.signal.aborted) props.store.report(error);
  }
}
async function downloadLog(): Promise<void> {
  if (!description.value || !parts.value.length || !props.visible || !savePicker) return;
  downloadOwner?.abort();
  const controller = new AbortController();
  downloadOwner = controller;
  const path =
    logBase.value +
    '/original?snapshotSha256=' +
    encodeURIComponent(String(description.value.snapshotSha256)) + invocationQuery('&');
  let writable: SaveSink | undefined;
  let completed = false;
  logDownloadBusy.value = true;
  try {
    const handle = await savePicker.call(window, { suggestedName: 'run-' + props.runId + '-log.txt' });
    controller.signal.throwIfAborted();
    writable = await handle.createWritable();
    controller.signal.throwIfAborted();
    const response = await props.store.client.response(path, controller.signal);
    if (!response.body) throw Error('log_stream_unavailable');
    await response.body.pipeTo(writable, { signal: controller.signal });
    completed = true;
  } catch (error) {
    if (!controller.signal.aborted && !(error instanceof DOMException && error.name === 'AbortError'))
      props.store.report(error);
  } finally {
    if (!completed && writable) {
      try {
        await writable.abort();
      } catch {
        /* pipeTo already aborted it */
      }
    }
    if (downloadOwner === controller) logDownloadBusy.value = false;
  }
}
async function downloadPart(): Promise<void> {
  if (!description.value || !parts.value.length || !props.visible) return;
  downloadOwner?.abort();
  const controller = new AbortController();
  downloadOwner = controller;
  const part = parts.value[partIndex.value];
  const path =
    logBase.value +
    '/parts/' +
    part.index +
    '?snapshotSha256=' +
    encodeURIComponent(String(description.value.snapshotSha256)) + invocationQuery('&');
  logDownloadBusy.value = true;
  try {
    const response = await props.store.client.response(path, controller.signal);
    const bytes = await boundedBytes(response, 1024 * 1024);
    controller.signal.throwIfAborted();
    downloads.save(
      new Blob([bytes as BlobPart], { type: 'text/plain;charset=utf-8' }),
      'run-' + props.runId + '-log-part-' + part.index + '.txt'
    );
  } catch (error) {
    if (!controller.signal.aborted) props.store.report(error);
  } finally {
    if (downloadOwner === controller) logDownloadBusy.value = false;
  }
}
function cancelLogDownload(): void {
  downloadOwner?.abort();
  logDownloadBusy.value = false;
}
watch(
  [() => props.runId, () => props.attemptId, () => props.visible],
  () => {
    logOwner?.abort();
    downloadOwner?.abort();
    downloads.release();
    selected.value = undefined;
    description.value = undefined;
    tails.clear();
    logText.value = '';
    logDownloadBusy.value = false;
    const first = props.initialLog && props.logs.find(log => historyLogId(log) === historyLogId(props.initialLog!)) || props.logs[0];
    if (props.visible && first) void selectLog(first);
  },
  { immediate: true }
);
watch(() => props.logs, logs => {
  if (!props.visible) return;
  if (selected.value && logs.some(log => historyLogId(log) === historyLogId(selected.value!))) return;
  if (logs[0]) void selectLog(logs[0]);
  else { logOwner?.abort(); selected.value = undefined; description.value = undefined; logText.value = ''; tails.clear(); }
});
onBeforeUnmount(() => {
  logOwner?.abort();
  downloadOwner?.abort();
  downloads.release();
});
</script>
<template>
  <nexus-modal :open.prop="visible" :title="t('查看完整日志')" size="wide" @nexus-close="emit('close')">
    <nav v-if="logs.length > 1" :aria-label="t('脚本日志')">
      <HistoryCatalogWindow :items="logs" :item-key="historyLogId" :label="t('脚本日志')" :visible="visible">
        <template #default="{ item: log }">
          <button :aria-pressed="!!selected && historyLogId(selected) === historyLogId(log)" @click="selectLog(log)">
            {{ log.key.stepId }} · {{ t(log.key.role) }}<template v-if="log.key.invocationOrdinal"> · {{ attemptTitle(log.key.invocationOrdinal) }}</template>
          </button>
        </template>
      </HistoryCatalogWindow>
    </nav>
    <nexus-scroll-area class="log-window" direction="both" :label="t('运行日志')" style="height: 50vh">
      <pre>{{ logText }}</pre>
    </nexus-scroll-area>
    <div class="toolbar">
      <button :disabled="partIndex <= 0" @click="readPart(partIndex - 1)">{{ t('上一段') }}</button>
      <span>{{ partIndex + 1 }} / {{ parts.length }}</span>
      <button :disabled="partIndex + 1 >= parts.length" @click="readPart(partIndex + 1)">
        {{ t('下一段') }}
      </button>
    </div>
    <button v-if="savePicker" slot="footer" :disabled="!parts.length || logDownloadBusy" @click="downloadLog">
      {{ t('下载原始日志') }}
    </button>
    <button v-else slot="footer" :disabled="!parts.length || logDownloadBusy" @click="downloadPart">
      {{ t('下载当前段') }}
    </button>
    <button v-if="logDownloadBusy" slot="footer" @click="cancelLogDownload">{{ t('取消下载') }}</button>
    <button slot="footer" @click="emit('close')">{{ t('关闭') }}</button>
  </nexus-modal>
</template>
