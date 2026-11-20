<script lang="ts">
import { computed as computedAvailability, inject as injectRegistry, onBeforeUnmount, ref } from 'vue';
import type { SurfaceId as AvailableSurface } from '@nexuspipeline/extension-sdk';
import { registryKey as availabilityRegistryKey } from './registry-context';

export function useSurfaceAvailability(target: AvailableSurface, extensionId: () => string | undefined) {
  const registry = injectRegistry(availabilityRegistryKey);
  const revision = ref(0);
  const unsubscribe = registry?.subscribe(() => { revision.value++; });
  onBeforeUnmount(() => unsubscribe?.());
  return computedAvailability(() => {
    void revision.value;
    return registry?.listSurfaces(target).some(entry => entry.extensionId === extensionId()) ?? false;
  });
}
</script>
<script setup lang="ts">
import { computed, inject } from 'vue';
import type { SurfaceId, SurfaceContext } from '@nexuspipeline/extension-sdk';
import type { SurfaceStore } from '../app/store-ports';
import { registryKey } from './registry-context';
import SurfaceOutlet from './SurfaceOutlet.vue';
const props = defineProps<{
  target: SurfaceId;
  store: SurfaceStore;
  visible: boolean;
  entityId?: string;
  runId?: string;
  attemptId?: string;
  entry?: { extensionId: string; id: string };
  extensionIds?: readonly string[];
}>();
const registry = inject(registryKey);
const context = computed<SurfaceContext>(() => ({
  surfaceId: props.target,
  instanceId: props.store.state.capabilities?.instanceId || '',
  ...(props.entityId ? { entityId: props.entityId } : {}),
  ...(props.runId ? { runId: props.runId } : {}),
  ...(props.attemptId ? { attemptId: props.attemptId } : {})
}));
</script>
<template>
  <SurfaceOutlet
    v-if="registry"
    :registry="registry"
    :target="target"
    :context="context"
    :visible="visible"
    :extension-ids="extensionIds"
    v-bind="entry ? { entry } : {}"
  />
</template>
