<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import type { ConfigurationStore } from '../app/store-ports';
import { locale, t } from '../app/locale';
import { objectValue } from '../control/client';
import { extensionDetails, extensionList, type ExtensionDetails } from '../control/extensions';
import { taskTitle } from '../control/extension-metadata';
import { createIdentity } from '../control/identity';
import AdapterCoverage from './AdapterCoverage.vue';
import AdapterDiagnostics from './AdapterDiagnostics.vue';
import AdapterEditorPanel from './AdapterEditorPanel.vue';
import TaskCards from './TaskCards.vue';
import TaskSummary from './TaskSummary.vue';
import { configurationDrafts } from './configuration-drafts';
import { configurationOverview, type ConfigurationOverview } from './configuration-types';
import { adapterCommand, adapterResult, adapterSelection, adapterTasks, type AdapterResult, type AdapterSource, type AdapterTask } from './adapter-results';
import './configuration-messages';

const props = defineProps<{ store: ConfigurationStore; bindingId: string; visible: boolean;
  draft?: { userId: string; scriptId: string; scriptRevision: number; candidateId: string | undefined } }>();
const overview = shallowRef<ConfigurationOverview>();
const source = shallowRef<AdapterSource>();
const details = shallowRef<ExtensionDetails>();
const result = shallowRef<AdapterResult>();
const tasks = shallowRef<AdapterTask[]>();
const issue = ref('');
const loading = ref(false);
const checking = ref(false);
const recovering = '配置正在恢复。请在设置的诊断中查看原因。';
const missing = '配置资源缺失，请先保存完整的用户配置。';
let owner: AbortController | undefined;
const instance = computed(() => props.store.state.capabilities?.instanceId);
const binding = computed(() => props.store.state.automation?.bundle.bindings.find(item => item.id === props.bindingId));
const bindingRevision = computed(() => props.store.state.automation?.revisions.find(item =>
  item.kind === 'binding' && item.id === props.bindingId)?.revision);
const scriptRevision = computed(() => props.store.state.automation?.revisions.find(item =>
  item.kind === 'script' && item.id === binding.value?.scriptId)?.revision);
const mutation = () => props.store.state.events?.mutationSequence;
const visibleKey = computed(() => JSON.stringify([
  props.visible, instance.value, props.bindingId, props.draft?.candidateId, props.draft?.userId, props.draft?.scriptId,
  props.draft?.scriptRevision, props.store.can('configuration.read'), props.store.can('extensions.read'),
  binding.value?.scriptId, bindingRevision.value, scriptRevision.value,
  props.store.state.events?.extensionGeneration, props.store.state.events?.recoveryRevision
]));

const cards = computed(() => tasks.value?.map(task => ({
  id: task.taskId, title: taskTitle(task, locale.value),
  label: t(!task.isEnabled ? '已停用' : task.isSelected ? '计划执行' : '未选择'),
  tone: task.isSelected && task.isEnabled ? 'info' : 'neutral'
})));
const resources = computed(() => overview.value?.resources.map(item => item.label || item.relativePath || item.resourceId).join(' · '));
const readOverview = (id: string, signal: AbortSignal) => props.store.client.read(
  '/control/v1/configuration/bindings/' + encodeURIComponent(id) + '/resources', configurationOverview, signal);

function discoveryIssue(reason: unknown): string {
  const code = reason instanceof Error ? reason.message : '';
  if (code === 'configuration_recovery_pending') return recovering;
  if (code === 'configuration_not_found' || code === 'configuration_original_lost') return missing;
  if (code === 'configuration_draft_missing') return code;
  return '读取任务计划失败，请重新探测。';
}

async function savedPlan(value: ConfigurationOverview, current: AbortController, isCurrent: () => boolean): Promise<void> {
  overview.value = value;
  const { extensionId, resources } = value;
  if (!extensionId) throw new Error('adapter_source_unbound');
  const signal = current.signal;
  const installed = await props.store.client.read('/control/v1/extensions', extensionList, signal);
  if (!isCurrent()) return;
  const extension = installed.items.find(item => item.extensionId === extensionId);
  if (!extension?.isInstalled || !extension.desiredEnabled || !extension.activationId || extension.installedRevision < 1) {
    issue.value = '扩展未安装或未启用，专项配置不可用。';
    return;
  }
  if (!resources.length || resources.some(item => !item.isSaved || item.revision < 1 || item.shape === 'missing' || item.fileCount < 1)) {
    issue.value = missing;
    return;
  }
  if (resources.every(item => item.syntax === null)) {
    issue.value = '请在项目配置编辑器中查看此项目的任务。';
    return;
  }
  const verified = { overview: value, installedRevision: extension.installedRevision };
  source.value = verified;
  const selection = adapterSelection(verified, createIdentity());
  void props.store.client.read('/control/v1/extensions/' + encodeURIComponent(extensionId) + '/details',
    row => extensionDetails(row, extensionId), signal).then(metadata => {
    if (isCurrent() && metadata.installedRevision === extension.installedRevision) details.value = metadata;
  }).catch(() => {});
  const discovered = await props.store.client.query('/control/v1/adapters/discover', adapterCommand('discover', selection),
    row => adapterResult(row, 'discover', selection, undefined, '', value.scriptRevision), signal);
  if (!isCurrent()) return;
  result.value = discovered;
  tasks.value = discovered.tasks;
}

async function load(prepared?: ConfigurationOverview): Promise<void> {
  owner?.abort();
  owner = undefined;
  overview.value = source.value = result.value = tasks.value = details.value = undefined;
  issue.value = '';
  loading.value = checking.value = false;
  if (!props.visible) return;
  if (!props.store.can('configuration.read') || (!props.draft && !props.store.can('extensions.read'))) {
    issue.value = '当前权限无法读取任务计划。';
    return;
  }
  const current = new AbortController();
  owner = current;
  const key = visibleKey.value, revision = mutation();
  const isCurrent = () => owner === current && !current.signal.aborted && visibleKey.value === key;
  const id = props.bindingId, draft = props.draft;
  loading.value = true;
  try {
    if (draft) {
      const read = await props.store.client.query('/control/v1/adapters/discover-draft',
        { ...draft, operationId: createIdentity(), bindingId: id }, row => adapterTasks(objectValue(row).tasks), current.signal);
      if (isCurrent()) tasks.value = read;
      return;
    }
    const read = prepared ?? await readOverview(id, current.signal);
    if (!isCurrent()) return;
    if (read.bindingId !== id || read.kind !== 'specialized' ||
      (binding.value && read.scriptId !== binding.value.scriptId) ||
      (bindingRevision.value !== undefined && read.bindingRevision !== bindingRevision.value) ||
      (scriptRevision.value !== undefined && read.scriptRevision !== scriptRevision.value))
      throw new Error('adapter_source_unbound');
    await savedPlan(read, current, isCurrent);
  } catch (reason) {
    if (isCurrent()) {
      issue.value = discoveryIssue(reason);
      if (issue.value === recovering || issue.value === missing) source.value = undefined;
    }
  } finally {
    if (owner === current) {
      loading.value = false;
      if (isCurrent() && mutation() !== revision) void verifyOverview();
    }
  }
}

async function verifyOverview(): Promise<void> {
  const current = owner, previous = overview.value;
  if (!current || current.signal.aborted || !previous || props.draft || checking.value) return;
  const key = visibleKey.value, revision = mutation();
  const isCurrent = () => owner === current && !current.signal.aborted && visibleKey.value === key;
  checking.value = true;
  try {
    const read = await readOverview(previous.bindingId, current.signal);
    if (isCurrent() && JSON.stringify(read) !== JSON.stringify(previous)) await load(read);
  } catch (reason) {
    if (isCurrent()) {
      current.abort();
      source.value = result.value = tasks.value = undefined;
      loading.value = false;
      issue.value = discoveryIssue(reason);
    }
  } finally {
    if (owner === current) {
      checking.value = false;
      if (isCurrent() && mutation() !== revision) void verifyOverview();
    }
  }
}

watch(visibleKey, () => void load(), { immediate: true });
watch(mutation, () => void verifyOverview());
watch([instance, () => props.bindingId], (_, __, cleanup) => {
  cleanup(configurationDrafts(props.store, props.bindingId).onChanged(() => void load()));
}, { immediate: true });
onBeforeUnmount(() => owner?.abort());
</script>

<template>
  <section class="binding-task-plan">
    <header>
      <h3>{{ t('任务计划') }}</h3>
      <button type="button" :disabled="loading" @click="load()">{{ t('重新探测') }}</button>
    </header>
    <div class="probe-source">
      <strong>{{ t('探测配置') }}</strong>
      <nexus-badge :label="t(draft ? '配置草稿' : '已保存快照')" />
    </div>
    <p v-if="resources" class="source-paths">{{ resources }}</p>
    <div class="plan-result" :aria-busy="loading">
      <nexus-spinner v-if="loading" :label="t('正在探测配置…')" />
      <p v-else-if="issue" role="status">{{ t(issue) }}</p>
      <p v-else-if="tasks && !tasks.length" role="status">{{ t('此配置没有专项任务。') }}</p>
      <template v-else-if="cards?.length">
        <TaskSummary :tasks="cards" :label="t('计划状态汇总')" />
        <TaskCards :tasks="cards" :label="t('任务计划')" />
      </template>
      <details v-if="result?.coverage || result?.diagnostics.length">
        <summary>{{ t('配置诊断') }}</summary>
        <AdapterCoverage :coverage="result?.coverage" :game-path-check="result?.gamePathCheck" />
        <AdapterDiagnostics :items="result?.diagnostics" :details="details" />
      </details>
    </div>
    <AdapterEditorPanel v-if="!draft" :key="instance + '/' + bindingId" :store="store" :binding-id="bindingId"
      :source="source" :details="details" :visible="visible" :checking="checking || loading" />
  </section>
</template>

<style scoped>
.binding-task-plan { display: grid; gap: 9px; min-width: 0; }
header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; }
h3 { margin: 0; font-size: 14px; }
.probe-source { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; font-size: 13px; }
.source-paths, .plan-result > p { margin: 0; color: var(--muted); overflow-wrap: anywhere; font-size: 12px; }
.plan-result { display: grid; gap: 9px; min-width: 0; }
.plan-result details {
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 9px;
  font-size: 12px;
}
summary { cursor: pointer; }
</style>
