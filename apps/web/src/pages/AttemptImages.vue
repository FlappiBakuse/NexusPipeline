<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { boundedBytes } from '../control/client';
import type { HistoryImage } from '../control/records';
import { historyReason } from './history-detail';
import { BlobDownloads } from '../control/blob-downloads';
import ImageGallery from '../components/ImageGallery.vue';
import HistoryImagePreview from './HistoryImagePreview.vue';
const props = defineProps<{
  store: AppStore;
  runId: string;
  attemptId: string;
  visible: boolean;
  images: HistoryImage[];
}>();
const displayed = ref<HistoryImage | null>(null);
const captured = computed(() => props.images.filter(image => image.outcome === 'captured'));
const unavailable = computed(() => props.images.filter(image => image.outcome === 'unavailable'));
const downloads = new BlobDownloads();
let owner: AbortController | undefined;
const base = computed(() => '/control/v1/records/' + props.runId + '/attempts/' + props.attemptId);
function imagePath(image: HistoryImage): string {
  const key = image.key;
  return (
    base.value +
    '/images/' +
    encodeURIComponent(String(key.stepId)) +
    '/' +
    encodeURIComponent(String(key.captureId)) +
    '/original?resultSha256=' +
    encodeURIComponent(String(image.resultSha256))
  );
}
async function download(): Promise<void> {
  if (!displayed.value || !props.visible) return;
  owner?.abort();
  const controller = new AbortController();
  owner = controller;
  try {
    const response = await props.store.client.response(imagePath(displayed.value), controller.signal);
    const bytes = await boundedBytes(response, 32 * 1024 * 1024);
    controller.signal.throwIfAborted();
    downloads.save(
      new Blob([bytes as BlobPart], { type: response.headers.get('content-type') || 'image/png' }),
      'run-' + props.runId + '-capture.png'
    );
  } catch (reason) {
    if (!controller.signal.aborted) props.store.report(reason);
  }
}
function close(): void {
  owner?.abort();
  downloads.release();
  displayed.value = null;
}
watch(() => [props.runId, props.attemptId, props.visible], close);
onBeforeUnmount(close);
</script>
<template>
  <ImageGallery
    :pool="store.media"
    :paths="captured.map(imagePath)"
    :visible="visible"
    @select="displayed = captured[$event] || null"
  />
  <p v-for="image in unavailable" :key="image.key.captureId" role="status" class="history-image-unavailable">
    {{ historyReason(image.reasonCode) }}
  </p>
  <!-- Keep one accessibility identity across opens; visibility controls media release. -->
  <HistoryImagePreview
    :pool="store.media"
    :path="displayed ? imagePath(displayed) : ''"
    :reason="historyReason(displayed?.reasonCode || '')"
    :visible="visible && displayed !== null"
    @close="close"
    @download="download"
  />
</template>
