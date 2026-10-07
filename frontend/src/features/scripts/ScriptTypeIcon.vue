<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";
import projectIcon from "../../../../src/NexusPipeline.ico?url";
import { apiBlob } from "../../platform/api";
const props = defineProps<{ typeId: string }>();
const source = ref(projectIcon);
let controller: AbortController | null = null;
let objectUrl = "";
async function load() {
  controller?.abort();
  if (objectUrl) URL.revokeObjectURL(objectUrl);
  objectUrl = ""; source.value = projectIcon;
  if (props.typeId === "general") return;
  const current = new AbortController(); controller = current;
  try {
    const blob = await apiBlob(`/api/scripts/type-icons/${encodeURIComponent(props.typeId)}`, current.signal);
    if (current.signal.aborted || controller !== current || !blob.type.startsWith("image/")) return;
    objectUrl = URL.createObjectURL(blob); source.value = objectUrl;
  } catch { }
}
watch(() => props.typeId, () => void load(), { immediate: true });
onBeforeUnmount(() => { controller?.abort(); controller = null; if (objectUrl) URL.revokeObjectURL(objectUrl); });
</script>

<template><span class="script-type-icon" aria-hidden="true"><img :src="source" alt="" @error="source = projectIcon" /></span></template>
<style>
.script-type-icon { display: grid; flex: 0 0 36px; width: 36px; height: 36px; place-items: center; }
.script-type-icon img { display: block; width: 100%; height: 100%; object-fit: contain; }
</style>
