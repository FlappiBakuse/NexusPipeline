<script setup lang="ts">
import { createIdentity } from '../control/identity';
import { computed, onBeforeUnmount, reactive, toRefs, watch } from 'vue';
import type { ConfigurationStore } from '../app/store-ports';
import { ControlFailure, integerValue, objectValue, stringValue } from '../control/client';
import { t } from '../app/locale';
import type { ConfigurationOverview } from './configuration-types';
import './configuration-messages';

type Session = { sessionId: string; revision: number; phase: string };
type Draft = {
  rootId: string;
  relativePath: string;
  argumentsText: string;
  resourceIds: string[];
  session: Session | null;
  pending: { action: string; operationId: string; sessionId: string } | null;
  launchId: string;
  busy: boolean;
  note: string;
};
const props = defineProps<{ store: ConfigurationStore; overview: ConfigurationOverview; visible: boolean }>();
const emit = defineEmits<{ changed: [] }>();
const key = 'configuration/native/' + props.overview.bindingId;
const saved = props.store.drafts.get(key);
const state = reactive(
  saved || {
    rootId: props.store.state.automation?.bundle.scripts.find(script => script.id === props.overview.scriptId)?.executable.rootId || '',
    relativePath: props.store.state.automation?.bundle.scripts.find(script => script.id === props.overview.scriptId)?.executable.relative || '',
    argumentsText: '',
    resourceIds: props.overview.resources.map(resource => resource.resourceId),
    session: null,
    pending: null,
    launchId: '',
    busy: false,
    note: ''
  }
) as Draft;
props.store.drafts.set(key, state);
watch(
  () => [state.rootId, state.relativePath, state.argumentsText, state.resourceIds],
  () => props.store.dirty.add(key),
  { deep: true, flush: 'sync' }
);
const roots = computed(() => props.store.state.automation?.roots || []);
const { busy, note } = toRefs(state);
let alive = true;
const canBegin = computed(
  () =>
    !busy.value &&
    !state.pending &&
    !state.session &&
    !!state.rootId &&
    !!state.relativePath.trim() &&
    state.resourceIds.length > 0 &&
    state.resourceIds.length <= 32
);
function toggle(id: string): void {
  state.resourceIds = state.resourceIds.includes(id)
    ? state.resourceIds.filter((value) => value !== id)
    : [...state.resourceIds, id];
}
function terminal(phase: string): boolean {
  return ['saved', 'cancelled', 'restored'].includes(phase.toLowerCase());
}
function editorSession(value: unknown): Session {
  const row = objectValue(value);
  return {
    sessionId: stringValue(row.sessionId),
    revision: integerValue(row.revision),
    phase: stringValue(row.phase)
  };
}
async function readSession(): Promise<void> {
  const id = state.session?.sessionId || state.pending?.sessionId || state.launchId;
  if (!id) return;
  try {
    const value = await props.store.client.read('/control/v1/configuration/editor/' + id, objectValue);
    const session = editorSession(value);
    if (terminal(session.phase)) {
      if (
        state.pending &&
        state.pending.action !== 'begin' &&
        value.completionOperationId !== state.pending.operationId
      ) {
        note.value = '会话由其他操作结束，请核实原操作。';
        return;
      }
      state.session = null;
      state.pending = null;
      state.launchId = '';
      note.value = session.phase.toLowerCase() === 'saved' ? '已保存' : '已取消';
      props.store.dirty.delete(key);
      if (alive) emit('changed');
    } else {
      state.session = session;
      if (state.pending?.action === 'begin') state.pending = null;
      note.value = '编辑会话仍在进行。';
    }
  } catch (error) {
    note.value = error instanceof Error ? error.message : '操作失败，请重试。';
  }
}
async function begin(): Promise<void> {
  if (!canBegin.value) return;
  const root = roots.value.find((item) => item.id === state.rootId);
  if (!root) {
    note.value = '请选择编辑器目录、程序与资源。';
    return;
  }
  if (!state.launchId) state.launchId = createIdentity();
  const selection = {
    format: 'nexus.control.native-editor',
    version: 1,
    sessionId: state.launchId,
    bindingId: props.overview.bindingId,
    expectedBindingRevision: props.overview.bindingRevision,
    expectedScriptRevision: props.overview.scriptRevision,
    editorRootId: root.id,
    expectedEditorRootRevision: root.revision,
    editorRelativePath: state.relativePath.trim().replace(/\\/g, '/'),
    arguments: state.argumentsText.split(/\r?\n/).filter((item) => item.length),
    resources: props.overview.resources
      .filter((item) => state.resourceIds.includes(item.resourceId))
      .map((item) => ({ resourceId: item.resourceId, expectedRevision: item.revision }))
  };
  busy.value = true;
  note.value = '';
  state.pending = { action: 'begin', operationId: state.launchId, sessionId: state.launchId };
  try {
    state.session = await props.store.client.write(
      '/control/v1/configuration/editor/begin',
      selection,
      editorSession,
      state.launchId
    );
    state.pending = null;
    note.value = '原生编辑器已启动。';
  } catch (error) {
    if (!(error instanceof ControlFailure && error.uncertain)) state.pending = null;
    note.value = error instanceof Error ? error.message : '操作失败，请重试。';
  } finally {
    busy.value = false;
  }
}
async function finish(action: 'save' | 'cancel'): Promise<void> {
  if (!state.session || state.pending || busy.value) return;
  const id = createIdentity(),
    sessionId = state.session.sessionId;
  busy.value = true;
  note.value = '';
  state.pending = { action, operationId: id, sessionId };
  try {
    await props.store.client.write(
      `/control/v1/configuration/editor/${sessionId}/${action}`,
      {
        format: 'nexus.control.native-editor-finish',
        version: 1,
        operationId: id,
        expectedSessionRevision: state.session.revision
      },
      (value) => {
        const row = objectValue(value);
        editorSession(action === 'save' ? row.session : row);
        return row;
      },
      id
    );
    await readSession();
  } catch (error) {
    if (!(error instanceof ControlFailure && error.uncertain)) state.pending = null;
    note.value = error instanceof Error ? error.message : '操作失败，请重试。';
  } finally {
    busy.value = false;
  }
}
watch(
  () => props.visible,
  (visible) => {
    if (visible && !busy.value && (state.session || state.pending)) void readSession();
  },
  { immediate: true }
);
onBeforeUnmount(() => {
  alive = false;
});
</script>
<template>
  <div class="config-fields">
    <template v-if="!state.session && !state.pending">
      <label class="form-field">
        {{ t('编辑器目录') }}
        <FormSelect v-model="state.rootId">
          <option value="">{{ t('编辑器目录') }}</option>
          <option v-for="root in roots" :key="root.id" :value="root.id">{{ root.path }}</option>
        </FormSelect>
      </label>
      <label class="form-field">
        {{ t('编辑器相对路径') }}
        <input v-model="state.relativePath" placeholder="tools/editor.exe" />
      </label>
      <label class="form-field">
        {{ t('编辑器参数') }}
        <nexus-text-area
          :value.prop="state.argumentsText"
          :aria-label="t('编辑器参数')"
          :placeholder="t('每行一个参数')"
          :rows.prop="3"
          @nexus-change="state.argumentsText = $event.detail.value"
        />
      </label>
      <fieldset>
        <legend>{{ t('编辑的资源') }}</legend>
        <label v-for="item in overview.resources" :key="item.resourceId" class="choice-row">
          <input
            type="checkbox"
            :checked="state.resourceIds.includes(item.resourceId)"
            @change="toggle(item.resourceId)"
          />
          {{ item.label }}
        </label>
      </fieldset>
      <button type="button" :disabled="!canBegin" @click="begin">{{ t('打开编辑器') }}</button>
    </template>
    <template v-else>
      <p v-if="state.session" role="status">{{ t('会话状态') }}: {{ state.session.phase }}</p>
      <p v-if="state.pending" role="alert">{{ t('会话结果待确认，请先读取会话状态。') }}</p>
      <div class="row">
        <button type="button" v-if="state.session" :disabled="busy || !!state.pending" @click="finish('save')">
          {{ t('保存编辑结果') }}
        </button>
        <button type="button" v-if="state.session" :disabled="busy || !!state.pending" @click="finish('cancel')">
          {{ t('取消并恢复') }}
        </button>
        <button type="button" :disabled="busy" @click="readSession">{{ t('读取会话状态') }}</button>
      </div>
    </template>
    <p v-if="note" role="status">{{ t(note) }}</p>
  </div>
</template>
