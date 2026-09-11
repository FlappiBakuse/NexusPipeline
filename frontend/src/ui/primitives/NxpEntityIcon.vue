<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";
import { apiBlob, isAbortError } from "../../platform/api";
import NxpIcon from "./NxpIcon.vue";

const props = withDefaults(
  defineProps<{ id?: string; fallback?: string; alt?: string }>(),
  { id: "", fallback: "script", alt: "" },
);

const source = ref("");
let objectUrl = "";
let controller: AbortController | null = null;
let loadSerial = 0;

function releaseObjectUrl() {
  if (objectUrl) URL.revokeObjectURL(objectUrl);
  objectUrl = "";
  source.value = "";
}

async function load() {
  const serial = ++loadSerial;
  controller?.abort();
  controller = null;
  releaseObjectUrl();
  if (!props.id) return;
  const nextController = new AbortController();
  controller = nextController;
  try {
    const blob = await apiBlob(
      `/api/scripts/${encodeURIComponent(props.id)}/icon`,
      nextController.signal,
    );
    if (serial !== loadSerial || nextController.signal.aborted || !blob.type.startsWith("image/")) return;
    objectUrl = URL.createObjectURL(blob);
    source.value = objectUrl;
  } catch (reason) {
    if (!isAbortError(reason)) {
      // Optional entity artwork falls back to the standard script glyph.
    }
  } finally {
    if (controller === nextController) controller = null;
  }
}

watch(() => props.id, () => void load(), { immediate: true });
onBeforeUnmount(() => {
  ++loadSerial;
  controller?.abort();
  controller = null;
  releaseObjectUrl();
});
</script>

<template>
  <span class="script-ico nxp-entity-icon" aria-hidden="true">
    <img v-if="source" :src="source" :alt="props.alt" loading="lazy" />
    <NxpIcon v-else :name="props.fallback" />
  </span>
</template>

<style>
.nxp-entity-icon img {
  width: 100%;
  height: 100%;
  border-radius: inherit;
  object-fit: contain;
}
</style>
