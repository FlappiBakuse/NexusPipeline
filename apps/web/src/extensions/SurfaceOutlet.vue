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
interface MountedEntry {
  extensionId: string;
  id: string;
  slot: HTMLElement;
  owner: AbortController;
  session?: SurfaceSession;
  pending?: Promise<void>;
  error?: unknown;
}
const entries = new Map<string, MountedEntry>();
let detached = false;
let unsubscribe: (() => void) | undefined;
let stopRetry: (() => void) | undefined;
const refreshError = () => {
  const error = [...entries.values()].find(entry => entry.error)?.error;
  errorText.value = error instanceof Error ? error.message : error ? String(error) : '';
};
function report(error: unknown) {
  emit('error', error);
  props.registry.reportMountFailure(error);
}
async function release(entry: MountedEntry) {
  entry.owner.abort(); entry.slot.remove();
  try { await entry.session?.scope.dispose(); } catch (error) { report(error); }
}
async function mount(entry: MountedEntry): Promise<void> {
  if (entry.pending) return entry.pending;
  entry.error = undefined; refreshError();
  entry.pending = (async () => {
    try {
      const session = await props.registry.mount(entry.extensionId, entry.id, entry.slot, props.context, entry.owner.signal);
      if (entry.owner.signal.aborted || detached) { await session.scope.dispose(); return; }
      entry.session = session;
      await nextTick(); session.setVisible(props.visible);
    } catch (error) {
      if (!entry.owner.signal.aborted && !detached) { entry.error = error; report(error); }
    } finally { entry.pending = undefined; refreshError(); }
  })();
  return entry.pending;
}
async function reconcile(changed?: string) {
  if (detached || !container.value) return;
  if (props.context.surfaceId !== props.target) { report(new Error('Surface target and context disagree')); return; }
  const wanted = (props.entry ? [props.entry] : props.registry.listSurfaces(props.target))
    .filter(entry => !props.extensionIds || props.extensionIds.includes(entry.extensionId));
  const keys = new Set(wanted.map(entry => entry.extensionId + '/' + entry.id));
  const retired: Promise<void>[] = [];
  for (const [key, entry] of entries) if (!keys.has(key) || entry.extensionId === changed) {
    entries.delete(key); retired.push(release(entry));
  }
  const pending: Promise<void>[] = [];
  for (const value of wanted) {
    const key = value.extensionId + '/' + value.id;
    if (entries.has(key)) continue;
    const slot = document.createElement('div'); slot.dataset.extensionId = value.extensionId;
    container.value.append(slot);
    const entry: MountedEntry = { ...value, slot, owner: new AbortController() };
    entries.set(key, entry); pending.push(mount(entry));
  }
  await Promise.all([...retired, ...pending]); refreshError();
}
async function retryFailed() {
  await Promise.all([...entries.values()].filter(entry => entry.error).map(mount));
}
function reset() {
  const previous = [...entries.values()]; entries.clear();
  for (const entry of previous) void release(entry);
  props.registry.trackMount(reconcile());
}
watch([() => props.registry, identity], reset, { flush: 'post' });
onMounted(() => {
  stopRetry = props.registry.subscribeRetry(retryFailed);
  unsubscribe = props.registry.subscribe(extensionId => {
    if (!extensionId || (props.entry && props.entry.extensionId !== extensionId)) return;
    if (props.extensionIds && !props.extensionIds.includes(extensionId)) return;
    props.registry.trackMount(reconcile(extensionId));
  });
  props.registry.trackMount(reconcile());
});
watch(() => props.visible, visible => {
  for (const entry of entries.values()) entry.session?.setVisible(visible);
});
onBeforeUnmount(() => {
  detached = true; unsubscribe?.(); stopRetry?.();
  for (const entry of entries.values()) void release(entry);
  entries.clear();
});
</script>

<template>
  <div class="surface-outlet">
    <p v-if="errorText" role="alert">{{ t('扩展界面加载失败：') }}{{ t(errorText) }}
      <button @click="registry.trackMount(retryFailed())">{{ t('重试') }}</button>
    </p>
    <div ref="container" class="surface-outlet-content" />
  </div>
</template>
<style scoped>
.surface-outlet:has(> .surface-outlet-content:empty):not(:has(> [role='alert'])) {
  display: none;
}
</style>
