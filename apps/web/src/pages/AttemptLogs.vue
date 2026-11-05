<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { arrayValue, objectValue, stringValue } from '../control/client';
import { attemptTitle } from './history-detail';
import HistoryLogViewer from './HistoryLogViewer.vue';
import { t } from '../app/locale';
const props = withDefaults(
  defineProps<{
    store: AppStore;
    runId: string;
    attemptId: string;
    visible: boolean;
    ordinal?: number;
    logs: (Record<string, unknown> & { key: { stepId: string; role: string; invocationOrdinal?: number } })[];
  }>(),
  { ordinal: 1 }
);
const expanded = ref(false);
const full = ref(false);
const text = ref('');
const omitted = ref(false);
const error = ref('');
const busy = ref(false);
const lines = computed(() => (text.value ? text.value.split('\n').length : 0));
const preview = computed(() => text.value.split('\n').slice(-200).join('\n'));
const title = computed(() => t('脚本日志') + '（' + attemptTitle(props.ordinal) + '）');
let owner: AbortController | undefined;
function decode(value: unknown): string {
  const encoded = stringValue(value);
  const bytes = Uint8Array.from(atob(encoded), (character) => character.charCodeAt(0));
  return new TextDecoder('utf-8').decode(bytes);
}
async function load(): Promise<void> {
  owner?.abort();
  if (!props.visible || !props.logs.length) return;
  const controller = new AbortController();
  owner = controller;
  busy.value = true;
  error.value = '';
  try {
    const texts: string[] = [];
    let truncated = false;
    for (const log of props.logs) {
      const key = log.key;
      const base =
        '/control/v1/records/' +
        props.runId +
        '/attempts/' +
        props.attemptId +
        '/logs/' +
        encodeURIComponent(key.stepId) +
        '/' +
        encodeURIComponent(key.role);
      const tail = await props.store.client.read(
        base + '/tail?maximumBytes=65536' + (key.invocationOrdinal ? '&invocationOrdinal=' + key.invocationOrdinal : ''),
        objectValue,
        controller.signal
      );
      controller.signal.throwIfAborted();
      const identity = objectValue(objectValue(tail.description).key);
      if (
        identity.runId !== props.runId ||
        identity.attemptId !== props.attemptId ||
        identity.stepId !== key.stepId ||
        identity.role !== key.role
      )
        throw Error('history_log_identity_invalid');
      texts.push(arrayValue(tail.slices, (item) => decode(objectValue(item).bytes)).join(''));
      truncated ||= tail.prefixOmitted === true;
    }
    text.value = texts.filter(Boolean).join('\n');
    omitted.value = truncated;
  } catch (reason) {
    if (!controller.signal.aborted) error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (owner === controller) busy.value = false;
  }
}
watch(
  [() => props.logs, () => props.visible],
  () => {
    if (!props.visible) {
      owner?.abort();
      full.value = false;
    } else void load();
  },
  { immediate: true }
);
watch(
  () => [props.runId, props.attemptId],
  () => {
    owner?.abort();
    text.value = '';
    omitted.value = false;
    expanded.value = false;
    full.value = false;
  }
);
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
      <span>{{ t('点击展开') }} {{ omitted ? '' : lines }} {{ t('行脚本日志') }}</span>
    </button>
    <template v-else>
      <p class="history-log-label">
        {{ title }}
        <span v-if="!omitted">· {{ lines }} {{ t('行脚本日志') }}</span>
      </p>
      <nexus-scroll-area class="history-log-preview" direction="both" :label="t('运行日志')">
        <pre>{{ preview || t('没有可用的日志') }}</pre>
      </nexus-scroll-area>
      <button v-if="omitted || lines > 200" class="history-log-full" @click="full = true">
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
