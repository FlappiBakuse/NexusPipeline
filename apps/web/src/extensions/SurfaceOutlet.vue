<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { SurfaceContext, SurfaceId } from '@nexuspipeline/extension-sdk';
import type { FrontendRegistry } from './frontend-registry.js';
import type { SurfaceSession } from './surface-session.js';
import { t } from '../app/locale';

const props = defineProps<{
  registry: FrontendRegistry;
  target: SurfaceId;
  context: SurfaceContext;
  visible: boolean;
  entry?: { extensionId: string; id: string };
  extensionIds?: readonly string[];
}>();
const emit = defineEmits<{ error: [error: unknown] }>();
const container = ref<HTMLElement | null>(null);
const errorText = ref('');
const identity = computed(() =>
  JSON.stringify([
    props.target,
    props.context.surfaceId,
    props.context.instanceId,
    props.context.entityId,
    props.context.runId,
    props.context.attemptId,
    props.entry?.extensionId,
    props.entry?.id,
    props.extensionIds
  ])
);
let generation = 0;
let sessions: SurfaceSession[] = [];
let detached = false;
let unsubscribe: (() => void) | undefined;

function report(error: unknown) {
  errorText.value = error instanceof Error ? error.message : String(error);
  emit('error', error);
  document.dispatchEvent(new CustomEvent('nexus-extension-error', { detail: error }));
}

async function dispose(surfaces: SurfaceSession[]) {
  for (const session of surfaces.reverse()) {
    try {
      await session.scope.dispose();
    } catch (error) {
      report(error);
    }
  }
}

async function replace() {
  const own = ++generation;
  const previous = sessions;
  sessions = [];
  await dispose(previous);
  if (detached || own !== generation) return;
  const host = container.value;
  if (!host) return;
  host.replaceChildren();
  errorText.value = '';
  if (props.context.surfaceId !== props.target) {
    report(new Error('Surface target and context disagree'));
    return;
  }
  const entries = (props.entry ? [props.entry] : props.registry.listSurfaces(props.target))
    .filter(entry => !props.extensionIds || props.extensionIds.includes(entry.extensionId));
  for (const entry of entries) {
    if (detached || own !== generation) return;
    const slot = document.createElement('div');
    slot.dataset.extensionId = entry.extensionId;
    host.append(slot);
    try {
      const session = await props.registry.mount(entry.extensionId, entry.id, slot, props.context);
      if (detached || own !== generation) {
        await dispose([session]);
        slot.remove();
        return;
      }
      sessions.push(session);
      await nextTick();
      session.setVisible(props.visible);
    } catch (error) {
      slot.remove();
      report(error);
    }
  }
}

watch(
  [() => props.registry, identity],
  () => {
    void replace();
  },
  { flush: 'post' }
);
onMounted(() => {
  unsubscribe = props.registry.subscribe((extensionId) => {
    if (props.entry && props.entry.extensionId !== extensionId) return;
    if (props.extensionIds && !props.extensionIds.includes(extensionId)) return;
    void replace();
  });
  void replace();
});
watch(
  () => props.visible,
  (visible) => {
    for (const session of sessions) session.setVisible(visible);
  }
);
onBeforeUnmount(() => {
  detached = true;
  unsubscribe?.();
  generation++;
  const previous = sessions;
  sessions = [];
  void dispose(previous);
});
</script>

<template>
  <div class="surface-outlet">
    <p v-if="errorText" role="alert">{{ t('扩展界面加载失败：') }}{{ t(errorText) }}</p>
    <div ref="container" class="surface-outlet-content" />
  </div>
</template>
<style scoped>
.surface-outlet:has(> .surface-outlet-content:empty):not(:has(> [role='alert'])) {
  display: none;
}
</style>
