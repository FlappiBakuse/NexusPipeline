<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle } from '../control/wire.generated';
import { arrayValue, objectValue, stringValue } from '../control/client';
import { BlobDownloads } from '../control/blob-downloads';
import { t } from '../app/locale';

const props = defineProps<{
  store: AutomationStore;
  script: AutomationBundle['scripts'][number];
  existing: boolean;
  locked: boolean;
}>();
const emit = defineEmits<{ import: [file: File]; error: [message: string] }>();
const picker = ref<HTMLInputElement>();
const busy = ref(false);
const downloads = new BlobDownloads();
const owner = new AbortController();

function choose(event: Event): void {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = '';
  if (file && !props.locked && !busy.value) emit('import', file);
}

async function exportScript(): Promise<void> {
  const revision = props.store.state.automation?.catalogRevision;
  if (busy.value || props.locked || revision === undefined || !props.store.can('automation.exchange')) return;
  const scriptId = props.script.id;
  const signal = AbortSignal.any([owner.signal, props.store.client.authoritySignal]);
  busy.value = true;
  try {
    const row = await props.store.client.read(
      '/control/v1/automation/export?revision=' +
        revision +
        '&templateScriptId=' +
        encodeURIComponent(scriptId),
      objectValue,
      signal
    );
    const bundle = objectValue(row.bundle) as unknown as AutomationBundle;
    if (
      bundle.format !== 'nexus.automation.bundle' ||
      bundle.version !== 1 ||
      bundle.exportKind !== 'scriptTemplate' ||
      bundle.scripts.length !== 1 ||
      bundle.scripts[0]?.id !== scriptId
    )
      throw new Error('导出结果与脚本不匹配。');
    const attachments = arrayValue(row.attachments, (item) => {
      const file = objectValue(item);
      return {
        id: stringValue(file.id),
        name: stringValue(file.packagePath),
        sha256: stringValue(file.sha256)
      };
    });
    const files: { name: string; data: Blob }[] = [];
    for (const file of attachments) {
      const response = await props.store.client.response(
        '/control/v1/automation/export/' +
          encodeURIComponent(file.id) +
          '/original?revision=' +
          revision +
          '&templateScriptId=' +
          encodeURIComponent(scriptId) +
          '&sourceSha256=' +
          file.sha256,
        signal
      );
      files.push({ name: file.name.split(/[\\/]/).pop() || file.id, data: await response.blob() });
    }
    signal.throwIfAborted();
    downloads.save(
      new Blob([JSON.stringify(bundle, null, 2)], { type: 'application/json' }),
      'NexusPipeline-template-' + scriptId + '.json'
    );
    for (const file of files) downloads.save(file.data, file.name);
  } catch (reason) {
    if (!signal.aborted) {
      emit('error', reason instanceof Error ? reason.message : '导出失败。');
    }
  } finally {
    busy.value = false;
  }
}

onBeforeUnmount(() => {
  owner.abort();
  downloads.release();
});
</script>

<template>
  <div v-if="script.kind === 'generic'" class="script-heading-actions">
    <button
      v-if="existing"
      type="button"
      :disabled="locked || busy || !store.can('automation.exchange')"
      @click="exportScript"
    >
      {{ t('导出') }}
    </button>
    <template v-else>
      <button type="button" :disabled="locked || !store.can('automation.exchange')" @click="picker?.click()">
        {{ t('导入') }}
      </button>
      <input ref="picker" type="file" accept=".json,application/json" hidden @change="choose" />
    </template>
  </div>
</template>

<style scoped>
.script-heading-actions {
  display: flex;
  align-items: center;
  flex: none;
}
button {
  min-width: 80px;
  color: var(--muted);
}
</style>
