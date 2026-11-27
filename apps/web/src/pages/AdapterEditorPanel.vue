<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, shallowRef, watch } from 'vue';
import { t } from '../app/locale';
import type { ConfigurationStore } from '../app/store-ports';
import FormSelect from '../components/FormSelect';
import { ControlFailure, objectValue } from '../control/client';
import type { ExtensionDetails } from '../control/extensions';
import { createIdentity } from '../control/identity';
import AdapterDiagnostics from './AdapterDiagnostics.vue';
import { adapterCommand, adapterResult, adapterSelection, type AdapterResult, type AdapterSource } from './adapter-contracts';
import { configurationDrafts } from './configuration-drafts';
import './configuration-messages';

const props = defineProps<{ store: ConfigurationStore; bindingId: string; source?: AdapterSource;
  details?: ExtensionDetails; visible: boolean; checking?: boolean }>();
const { store, bindingId } = props;
const instance = store.state.capabilities?.instanceId;
const key = 'configuration/adapter/' + instance + '/' + bindingId;
const bindingScope = configurationDrafts(store, bindingId);
const scope = bindingScope.child('adapter');
const write = scope.retain('state', () => {
  const state = reactive({ pending: '', busy: false, note: '', error: '' });
  scope.guardCancel(() => !state.pending);
  scope.onSettled(() => store.dirty.delete(key));
  return state;
});
const readAllowed = computed(() => store.can('configuration.read'));
const repairAllowed = computed(() => store.can('configuration.repair'));
const ready = computed(() => props.visible && readAllowed.value && !!props.source && !props.checking && !write.pending && !write.busy);
const proposal = shallowRef<{
  selection: ReturnType<typeof adapterSelection>;
  source: AdapterSource;
  result: AdapterResult;
}>();
const candidateIndex = ref(-1);
const candidate = computed(() => proposal.value?.result.candidates.find(item => item.index === candidateIndex.value));
const previewed = ref(false);
const reading = ref('');
const note = ref('');
const error = ref('');
let owner: AbortController | undefined;

watch(() => write.pending, pending => {
  if (pending) store.dirty.add(key);
  else store.dirty.delete(key);
}, { immediate: true, flush: 'sync' });
watch([() => props.visible, () => props.source, readAllowed, repairAllowed], invalidate);
watch(candidateIndex, () => { previewed.value = false; note.value = ''; error.value = ''; }, { flush: 'sync' });

function invalidate(): void {
  owner?.abort();
  owner = undefined;
  reading.value = '';
  proposal.value = undefined;
  candidateIndex.value = -1;
  previewed.value = false;
  note.value = error.value = '';
}

async function inspect(action: 'propose' | 'previewRepair'): Promise<void> {
  const source = props.source;
  if (!source || !ready.value || reading.value) return;
  const chosen = candidate.value, previous = proposal.value;
  if (action === 'previewRepair' && (!repairAllowed.value || !previous || !chosen)) return;
  if (action === 'propose') invalidate();
  previewed.value = false;
  note.value = error.value = write.note = write.error = '';
  const current = new AbortController();
  owner = current;
  reading.value = action;
  const selection = action === 'propose' ? adapterSelection(source, createIdentity()) : previous!.selection;
  const digest = previous?.result.proposalDigest || '';
  const isCurrent = () => owner === current && !current.signal.aborted && props.source === source &&
    props.visible && readAllowed.value && (action === 'propose' || repairAllowed.value && candidate.value === chosen);
  try {
    const result = await store.client.query('/control/v1/adapters/' + action,
      adapterCommand(action, selection, action === 'previewRepair' ? chosen : undefined, digest),
      row => adapterResult(row, action, selection, chosen, digest, source.overview.scriptRevision), current.signal);
    if (!isCurrent()) return;
    if (action === 'propose') {
      proposal.value = { source, selection, result };
      candidateIndex.value = result.candidates[0]?.index ?? -1;
      if (!result.candidates.length) note.value = '无候选修复。';
    } else {
      previewed.value = result.hasChanges === true;
      note.value = previewed.value ? '修复预览已完成。' : '无实际更改。';
    }
  } catch (reason) {
    if (isCurrent()) error.value = repairIssue(reason);
  } finally {
    if (owner === current) reading.value = '';
  }
}

function repairIssue(reason: unknown): string {
  if (reason instanceof ControlFailure && reason.status === 403) return '当前权限或宿主设置不允许执行此操作。';
  if (reason instanceof ControlFailure && reason.status === 409) return '配置或候选已变化，请重新探测并查找修复候选。';
  return '操作失败，请重试。';
}

function complete(changed: boolean): void {
  write.pending = '';
  invalidate();
  write.note = changed ? '修复已应用。' : '无实际更改。';
  if (!scope.signal.aborted) bindingScope.changed();
}

async function apply(): Promise<void> {
  const current = proposal.value, chosen = candidate.value;
  if (!ready.value || reading.value || !repairAllowed.value || !previewed.value ||
    !current || current.source !== props.source || !chosen) return;
  const { selection, result, source } = current;
  const digest = result.proposalDigest;
  write.busy = true;
  write.pending = selection.operationId;
  write.note = write.error = '';
  try {
    const saved = await store.client.write('/control/v1/adapters/applyRepair', adapterCommand('applyRepair', selection, chosen, digest),
      row => adapterResult(row, 'applyRepair', selection, chosen, digest, source.overview.scriptRevision), selection.operationId);
    complete(saved.hasChanges === true);
  } catch (reason) {
    if (!(reason instanceof ControlFailure && reason.uncertain)) {
      write.pending = '';
      previewed.value = false;
      write.error = repairIssue(reason);
    }
  } finally {
    write.busy = false;
  }
}

async function checkPending(): Promise<void> {
  const id = write.pending;
  if (!id || write.busy || !store.can('operations.read') || instance !== store.state.capabilities?.instanceId) return;
  write.busy = true;
  write.note = write.error = '';
  try {
    const result = await store.client.read('/control/v1/operations/' + id, objectValue);
    if (result.operationId === id && result.kind === 'configuration.save' && result.status === 'completed')
      complete(true);
    else write.note = '操作仍在处理中。';
  } catch (reason) {
    write.error = repairIssue(reason);
  } finally {
    write.busy = false;
  }
}

function candidateLabel(resourceId: string, editCount: number): string {
  const name = props.source?.overview.resources.find(resource => resource.resourceId === resourceId)?.label || resourceId;
  return name + ' · ' + t('{count} 项修改').replace('{count}', String(editCount));
}
onBeforeUnmount(() => {
  invalidate();
  if (!write.pending && !write.busy) scope.settle('disposed');
});
</script>

<template>
  <section class="configuration-repair">
    <header>
      <h4>{{ t('配置修复') }}</h4>
      <button type="button" :disabled="!ready || !!reading" @click="inspect('propose')">{{ t('查找修复候选') }}</button>
    </header>
    <div v-if="proposal?.result.candidates.length" class="repair-result">
      <label>
        {{ t('选择修复候选') }}
        <FormSelect v-model.number="candidateIndex" :disabled="!ready || !!reading">
          <option v-for="item in proposal.result.candidates" :key="item.index" :value="item.index">
            {{ candidateLabel(item.resourceId, item.editCount) }}
          </option>
        </FormSelect>
      </label>
      <div class="repair-actions">
        <button type="button" :disabled="!ready || !!reading || !candidate || !repairAllowed" @click="inspect('previewRepair')">{{ t('预览修复') }}</button>
        <button type="button" class="primary" :disabled="!ready || !!reading || !previewed || !repairAllowed" @click="apply">{{ t('应用修复') }}</button>
      </div>
    </div>
    <AdapterDiagnostics :items="proposal?.result.diagnostics" :details="details" />
    <div v-if="write.pending" class="repair-actions">
      <button type="button" :disabled="write.busy || !store.can('operations.read')" @click="checkPending">{{ t('查询操作状态') }}</button>
    </div>
    <p v-if="reading || note || error" role="status">
      <template v-if="previewed && candidate">{{ candidateLabel(candidate.resourceId, candidate.editCount) }} · </template>
      {{ t(reading === 'propose' ? '正在查找修复候选…' : reading ? '正在预览修复…' : error || note) }}
    </p>
    <template v-if="visible && !write.busy">
      <nexus-toast v-if="write.error" :tone="write.pending ? 'warning' : 'danger'" :message.prop="t(write.error)" />
      <nexus-toast v-else-if="write.note" tone="info" :message.prop="t(write.note)" />
      <nexus-toast v-else-if="write.pending" tone="warning" :message.prop="t('修复操作待确认，请先查询操作状态。')" />
    </template>
  </section>
</template>

<style scoped>
.configuration-repair { display: grid; gap: 8px; min-width: 0; padding-block-start: 10px; border-block-start: 1px solid var(--border); }
header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; }
h4 { margin: 0; font-size: 13px; }
.repair-result { display: grid; gap: 9px; padding-block-start: 10px; border-block-start: 1px solid var(--border); }
label { display: grid; gap: 5px; min-width: 0; color: var(--muted); font-size: 12px; }
.repair-actions { display: flex; flex-wrap: wrap; justify-content: end; gap: 7px; }
p { margin: 0; color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }
</style>
