<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, toRefs, watch } from 'vue';
import type { ConfigurationStore } from '../app/store-ports';
import { t } from '../app/locale';
import JsonValueInput from '../components/JsonValueInput.vue';
import type { ConfigurationOverview } from './configuration-types';
import { ConfigurationDocument, type ConfigurationValueDraft } from './configuration-document';
import { ConfigurationValueCommands } from './configuration-value-commands';
import './configuration-messages';

const props = defineProps<{ store: ConfigurationStore; overview: ConfigurationOverview; visible: boolean }>();
const emit = defineEmits<{ changed: [] }>();
const key =
  'configuration/value/' + props.store.state.capabilities?.instanceId + '/' + props.overview.bindingId;
const state = reactive(
  props.store.drafts.get(key) || {
    resourceId: '',
    filePath: '',
    syntax: 'json',
    path: '',
    expected: null,
    value: null,
    preview: '',
    operationId: '',
    pending: '',
    pendingAction: '',
    sourceSha256: '',
    bindingRevision: props.overview.bindingRevision,
    resourceRevision: 0,
    busy: false,
    note: ''
  }
) as ConfigurationValueDraft;
props.store.drafts.set(key, state);
watch(
  () => [
    state.resourceId,
    state.filePath,
    state.syntax,
    state.path,
    state.expected,
    state.value,
    state.expectedText,
    state.valueText
  ],
  () => props.store.dirty.add(key),
  { deep: true, flush: 'sync' }
);
const document = new ConfigurationDocument(props.store, state, () => props.overview);
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
});
</script>
<template>
  <div class="config-fields">
    <label class="form-field">
      {{ t('选择资源') }}
      <FormSelect
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
          v-model="state.path"
          :disabled="busy || !!state.pending"
          placeholder="/account/token"
          @input="changed"
        />
        <small>{{ t('字段路径使用 / 分隔，例如 /account/token。') }}</small>
      </label>
      <JsonValueInput
        :key="'expected/' + state.resourceId + '/' + state.filePath + state.path"
        v-model="state.expected"
        v-model:raw-value="state.expectedText"
        :label="t('原值')"
        :disabled="busy || !!state.pending"
        @update:raw-value="changed"
        @update:model-value="changed"
      />
      <JsonValueInput
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
