<script setup lang="ts">
import DraftNotice from '../components/DraftNotice.vue';
import { draftPresentation, draftDirective as vDraft } from '../app/draft-presentation';
import { computed, onBeforeUnmount, reactive, toRefs, watch } from 'vue';
import type { ConfigurationStore } from '../app/store-ports';
import { t } from '../app/locale';
import JsonValueInput from '../components/JsonValueInput.vue';
import type { ConfigurationOverview } from './configuration-types';
import { ConfigurationDocument, type ConfigurationValueDraft } from './configuration-document';
import { ConfigurationValueCommands } from './configuration-value-commands';
import { configurationDrafts } from './configuration-drafts';
import './configuration-messages';

const props = defineProps<{ store: ConfigurationStore; overview: ConfigurationOverview; visible: boolean }>();
const emit = defineEmits<{ changed: [] }>();
const key =
  'configuration/value/' + props.store.state.capabilities?.instanceId + '/' + props.overview.bindingId;
const scope = configurationDrafts(props.store, props.overview.bindingId).child('value');
const store = props.store;
const presentation = draftPresentation(scope.child('host-form'), true, () => document.edited || !!state.pending);
const state = scope.retain('state', () => {
  const state = reactive({
    resourceId: '',
    filePath: '',
    syntax: 'json',
    path: '',
    expected: null,
    value: null,
    baseline: '[null,null]',
    preview: '',
    operationId: '',
    pending: '',
    pendingAction: '',
    sourceSha256: '',
    bindingRevision: props.overview.bindingRevision,
    resourceRevision: 0,
    busy: false,
    note: ''
   }) as ConfigurationValueDraft;
  scope.guardCancel(() => !state.pending);
  scope.onSettled(() => store.dirty.delete(key));
  return state;
});
const document = new ConfigurationDocument(props.store, state, () => props.overview);
watch(
  () => document.edited || !!state.pending && state.pendingAction === 'apply',
  (changed) => {
    if (changed) props.store.dirty.add(key);
    else props.store.dirty.delete(key);
  },
  { immediate: true, flush: 'sync' }
);
let alive = true;
const commands = new ConfigurationValueCommands(
  props.store,
  state,
  document,
  props.overview.bindingId,
  key,
  () => {
    if (alive) emit('changed');
  }
);
const { note, busy } = toRefs(state);
const files = computed(() => document.view.files);
const fields = computed(() => document.view.fields);
const resource = computed(() => document.resource);
const file = computed(() => document.file);
const canEdit = computed(() => commands.canEdit);
const chooseResource = (id: string) => document.chooseResource(id);
const chooseFile = (path: string, keepSyntax = false) => document.chooseFile(path, keepSyntax);
const chooseField = (path: string) => document.chooseField(path);
function changed(): void {
  state.preview = '';
  state.operationId = '';
}
watch(
  () => props.visible,
  (visible) => {
    if (visible) void document.restore();
    else document.dispose();
  },
  { immediate: true }
);
onBeforeUnmount(() => {
  alive = false;
  document.dispose();
  if (!document.edited && !state.pending) scope.settle('disposed');
});
</script>
<template>
  <div v-draft="presentation" class="config-fields" :data-draft-item="state.resourceId + '/' + state.filePath + '/' + state.path">
    <DraftNotice :restored="presentation.state.restored" />
    <label class="form-field">
      {{ t('选择资源') }}
      <FormSelect
        data-draft-field="ValueEditorPanel/state.resourceId"
        :value="state.resourceId"
        :disabled="busy || !!state.pending"
        @change="chooseResource(($event.target as HTMLSelectElement).value)"
      >
        <option value="">{{ t('选择资源') }}</option>
        <option v-for="item in overview.resources" :key="item.resourceId" :value="item.resourceId">
          {{ item.label }} · {{ item.fileCount }}
        </option>
      </FormSelect>
    </label>
    <label v-if="files.length" class="form-field">
      {{ t('选择文件') }}
      <FormSelect
        data-draft-field="ValueEditorPanel/state.filePath"
        :value="state.filePath"
        :disabled="busy || !!state.pending"
        @change="chooseFile(($event.target as HTMLSelectElement).value)"
      >
        <option v-if="!files.some((item) => item.relativePath === '')" value="">{{ t('选择文件') }}</option>
        <option v-for="item in files" :key="item.relativePath" :value="item.relativePath">
          {{ item.relativePath || resource?.label }}
        </option>
      </FormSelect>
    </label>
    <label v-if="file && fields.length" class="form-field">
      {{ t('选择字段') }}
      <FormSelect
        data-draft-field="ValueEditorPanel/state.path"
        :value="state.path"
        :disabled="busy || !!state.pending"
        @change="chooseField(($event.target as HTMLSelectElement).value)"
      >
        <option value="">{{ t('选择字段') }}</option>
        <option v-for="item in fields" :key="item.path" :value="item.path">{{ item.path }}</option>
      </FormSelect>
    </label>
    <template v-if="file">
      <label class="form-field">
        {{ t('配置语法') }}
        <FormSelect
        data-draft-field="ValueEditorPanel/state.syntax"
          v-model="state.syntax"
          :disabled="busy || !!state.pending"
          @change="chooseFile(state.filePath, true)"
        >
          <option value="json">JSON</option>
          <option value="jsonWithComments">JSONC</option>
          <option value="yaml">YAML</option>
        </FormSelect>
      </label>
      <label class="form-field">
        {{ t('字段路径') }}
        <input
        data-draft-field="ValueEditorPanel/state.path"
          v-model="state.path"
          :disabled="busy || !!state.pending"
          placeholder="/account/token"
          @input="changed"
        />
        <small>{{ t('字段路径使用 / 分隔，例如 /account/token。') }}</small>
      </label>
      <JsonValueInput
        data-draft-field="ValueEditorPanel/state.expected"
        :key="'expected/' + state.resourceId + '/' + state.filePath + state.path"
        v-model="state.expected"
        v-model:raw-value="state.expectedText"
        :label="t('原值')"
        :disabled="busy || !!state.pending"
        @update:raw-value="changed"
        @update:model-value="changed"
      />
      <JsonValueInput
        data-draft-field="ValueEditorPanel/state.value"
        :key="'value/' + state.resourceId + '/' + state.filePath + state.path"
        v-model="state.value"
        v-model:raw-value="state.valueText"
        :label="t('新值')"
        :disabled="busy || !!state.pending"
        @update:raw-value="changed"
        @update:model-value="changed"
      />
    </template>
    <p v-if="note" role="status">{{ t(note) }}</p>
    <p v-if="state.pending" role="alert">
      {{ t('写入结果待确认，请先核实原操作。') }}
      <button type="button" @click="commands.inspect()">
        {{ t(state.pendingAction === 'preview' ? '读取资源重新开始' : '查询操作状态') }}
      </button>
    </p>
    <div class="row">
      <button type="button" :disabled="!canEdit" @click="commands.execute('preview')">{{ t('预览修改') }}</button>
      <button type="button" :disabled="!canEdit || !state.preview" @click="commands.execute('apply')">
        {{ t('保存配置') }}
      </button>
    </div>
  </div>
</template>
