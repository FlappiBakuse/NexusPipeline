<script setup lang="ts">
import { createIdentity } from '../control/identity';
import { computed, onBeforeUnmount, reactive, toRefs, watch } from 'vue';
import type { AppStore } from '../app/store';
import { ControlFailure, objectValue } from '../control/client';
import { t, locale } from '../app/locale';
import AdapterCoverage from './AdapterCoverage.vue';
import { taskTitle } from '../control/extension-metadata';
import { configurationOverview, type ConfigurationOverview } from './configuration-types';
import './configuration-messages';
import { useAdapterAvailability } from './adapter-availability';
import {
  adapterResult,
  resetAdapterProposal,
  type AdapterDraft as Draft
} from './adapter-results';
const props = defineProps<{ store: AppStore; overview: ConfigurationOverview; visible: boolean }>();
const emit = defineEmits<{ changed: [] }>();
const instance = props.store.state.capabilities?.instanceId;
const currentInstance = () => instance === props.store.state.capabilities?.instanceId;
const key = 'configuration/adapter/' + instance + '/' + props.overview.bindingId;
const saved = props.store.drafts.get(key);
const state = reactive(
  saved || {
    resourceIds: [] as string[],
    operationId: '',
    proposalDigest: '',
    candidateIndex: -1,
    previewed: false,
    pending: '',
    pendingAction: '',
    candidates: [] as Draft['candidates'],
    tasks: [] as Draft['tasks'],
    diagnostics: [] as { code: string; severity: string }[],
    request: null,
    busy: false,
    note: ''
  }
) as Draft;
props.store.drafts.set(key, state);
watch(
  () => [state.resourceIds, state.candidateIndex],
  () => props.store.dirty.add(key),
  { deep: true, flush: 'sync' }
);
const { busy, note } = toRefs(state);
let alive = true;
const { extension, details, loadingExtension, available } = useAdapterAvailability(
  props.store,
  () => props.overview.extensionId,
  () => props.visible,
  (reason) => {
    note.value = reason instanceof Error ? reason.message : '扩展未安装或未启用，专项配置不可用。';
  }
);
const selected = computed(() =>
  props.overview.resources.filter((item) => state.resourceIds.includes(item.resourceId) && item.isSaved)
);
const candidate = computed(() => state.candidates.find((item) => item.index === state.candidateIndex));
const resetProposal = () => resetAdapterProposal(state);
function toggle(id: string): void {
  state.resourceIds = state.resourceIds.includes(id)
    ? state.resourceIds.filter((item) => item !== id)
    : [...state.resourceIds, id];
  resetProposal();
}
function selection(id: string): Record<string, unknown> {
  return {
    operationId: id,
    bindingId: props.overview.bindingId,
    expectedBindingRevision: props.overview.bindingRevision,
    extensionId: props.overview.extensionId,
    expectedInstalledRevision: Number(extension.value?.installedRevision),
    resources: selected.value.map((item) => ({
      resourceId: item.resourceId,
      expectedRevision: item.revision,
      relativePath: item.relativePath
    }))
  };
}
async function execute(action: 'discover' | 'propose' | 'previewRepair' | 'applyRepair'): Promise<void> {
  if (
    !currentInstance() ||
    busy.value ||
    state.pending ||
    !available.value ||
    !props.store.can(
      action === 'previewRepair' || action === 'applyRepair' ? 'configuration.repair' : 'configuration.read'
    )
  )
    return;
  if (!selected.value.length) {
    note.value = '请先选择资源。';
    return;
  }
  if (
    (action === 'previewRepair' || action === 'applyRepair') &&
    (!candidate.value || !state.proposalDigest)
  ) {
    note.value = '请先查找并选择修复候选。';
    return;
  }
  if (action === 'applyRepair' && !state.previewed) {
    note.value = '需要先预览更改。';
    return;
  }
  if (action === 'discover' || action === 'propose') resetProposal();
  if (!state.operationId) state.operationId = createIdentity();
  const operationId = state.operationId;
  if (!state.request) state.request = selection(operationId);
  const body = {
    format: 'nexus.control.adapter-command',
    version: 1,
    action,
    selection: state.request,
    ...(action === 'previewRepair' || action === 'applyRepair'
      ? { candidateIndex: state.candidateIndex, proposalDigest: state.proposalDigest }
      : {})
  };
  busy.value = true;
  note.value = '';
  state.pending = operationId;
  state.pendingAction = action;
  try {
    const result = await props.store.client.write(
      '/control/v1/adapters/' + action,
      body,
      (value) => adapterResult(value, action, state.request!, candidate.value, state.proposalDigest),
      operationId
    );
    if (!currentInstance()) return;
    state.pending = '';
    state.pendingAction = '';
    if (action === 'discover') {
      state.coverage = result.coverage;
      state.gamePathCheck = result.gamePathCheck;
      state.tasks = result.tasks;
      state.diagnostics = result.diagnostics;
    } else if (action === 'propose') {
      state.proposalDigest = result.proposalDigest;
      state.candidates = result.candidates;
      state.diagnostics = result.diagnostics;
      state.candidateIndex = state.candidates[0]?.index ?? -1;
      state.previewed = false;
      if (!state.candidates.length) note.value = '无候选修复。';
    } else if (action === 'previewRepair') {
      state.previewed = true;
      note.value = result.hasChanges === false ? '无实际更改。' : '修复预览已完成。';
    } else {
      state.pending = '';
      resetProposal();
      note.value = '修复已应用。';
      props.store.dirty.delete(key);
      if (alive) emit('changed');
    }
  } catch (error) {
    if (!(error instanceof ControlFailure && error.uncertain)) {
      state.pending = '';
      state.pendingAction = '';
    }
    props.store.report(error);
  } finally {
    busy.value = false;
  }
}
async function checkPending(): Promise<void> {
  if (!state.pending || busy.value || !currentInstance()) return;
  try {
    if (state.pendingAction !== 'applyRepair') {
      await props.store.client.read(
        '/control/v1/configuration/bindings/' + props.overview.bindingId + '/resources',
        configurationOverview
      );
      state.pending = '';
      state.pendingAction = '';
      resetProposal();
      if (alive) emit('changed');
      return;
    }
    const result = await props.store.client.read('/control/v1/operations/' + state.pending, objectValue);
    if (
      result.operationId === state.pending &&
      result.kind === 'configuration.save' &&
      result.status === 'completed'
    ) {
      state.pending = '';
      state.pendingAction = '';
      resetProposal();
      note.value = '修复已应用。';
      if (alive) emit('changed');
    } else note.value = '操作仍在处理中。';
  } catch (error) {
    note.value = '无法证明保存结果；草稿已保留。';
    props.store.report(error);
  }
}
onBeforeUnmount(() => {
  alive = false;
});
</script>
<template>
  <AdapterCoverage :coverage="state.coverage" :game-path-check="state.gamePathCheck" />
  <div class="config-fields">
    <p v-if="loadingExtension" role="status">{{ t('正在读取配置资源…') }}</p>
    <p v-else-if="!available" role="status">{{ t('扩展未安装或未启用，专项配置不可用。') }}</p>
    <fieldset :disabled="busy || !!state.pending || !available">
      <legend>{{ t('选择要检查的资源') }}</legend>
      <label v-for="item in overview.resources" :key="item.resourceId" class="choice-row">
        <input
          type="checkbox"
          :checked="state.resourceIds.includes(item.resourceId)"
          :disabled="!item.isSaved"
          @change="toggle(item.resourceId)"
        />
        {{ item.label }}
        <small v-if="!item.isSaved">{{ t('配置资源尚未保存，无法编辑字段。') }}</small>
      </label>
    </fieldset>
    <div class="row">
      <button
        :disabled="busy || !!state.pending || !available || !selected.length"
        @click="execute('discover')"
      >
        {{ t('探测配置') }}
      </button>
      <button
        :disabled="busy || !!state.pending || !available || !selected.length"
        @click="execute('propose')"
      >
        {{ t('查找修复候选') }}
      </button>
    </div>
    <section v-if="state.tasks.length">
      <h3>{{ t('探测配置') }}</h3>
      <ul>
        <li v-for="item in state.tasks" :key="item.taskId">
          {{ taskTitle(item, locale) }} · {{ item.isEnabled ? '✓' : '—' }}
        </li>
      </ul>
    </section>
    <section v-if="state.diagnostics.length">
      <h3>{{ t('只读详情') }}</h3>
      <ul>
        <li v-for="(item, index) in state.diagnostics" :key="index">{{ item.severity }}: {{ details?.diagnosticLocales[locale]?.[item.code] || details?.diagnosticLocales[details.metadata?.defaultLocale || 'zh-CN']?.[item.code] || item.code }}</li>
      </ul>
    </section>
    <label v-if="state.candidates.length" class="form-field">
      {{ t('选择修复候选') }}
      <FormSelect
        v-model.number="state.candidateIndex"
        :disabled="busy || !!state.pending"
        @change="state.previewed = false"
      >
        <option v-for="item in state.candidates" :key="item.index" :value="item.index">
          {{
            overview.resources.find((resource) => resource.resourceId === item.resourceId)?.label ||
            item.resourceId
          }}
          · {{ item.editCount }}
        </option>
      </FormSelect>
    </label>
    <div v-if="candidate" class="row">
      <button :disabled="busy || !!state.pending || !available" @click="execute('previewRepair')">
        {{ t('预览修复') }}
      </button>
      <button
        :disabled="busy || !!state.pending || !state.previewed || !store.can('configuration.repair')"
        @click="execute('applyRepair')"
      >
        {{ t('应用修复') }}
      </button>
    </div>
    <p v-if="state.pending" role="alert">
      {{
        t(
          state.pendingAction === 'applyRepair'
            ? '修复操作待确认，请先查询操作状态。'
            : '读取结果未知，请重新读取资源。'
        )
      }}
      <button @click="checkPending">
        {{ t(state.pendingAction === 'applyRepair' ? '查询操作状态' : '读取资源重新开始') }}
      </button>
    </p>
    <p v-if="note" role="status">{{ t(note) }}</p>
  </div>
</template>
