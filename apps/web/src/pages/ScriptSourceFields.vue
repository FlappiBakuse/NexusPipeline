<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle, ScriptSource, ScriptSourceCandidate } from '../control/wire.generated';
import { extensionList, extensionDetails, type ExtensionDetails } from '../control/extensions';
import { createIdentity } from '../control/identity';
import { scriptPaths } from './script-paths';
import { readSource, selectSource, scriptSourceKey } from './script-source';
import { locale, t, registerMessages } from '../app/locale';
import OkwwAccountPreview from './OkwwAccountPreview.vue';
registerMessages({
  '请在 Host 本机创建专项脚本实例。': 'Create specialized script instances on the Host machine.',
  '选择脚本配置': 'Select script configuration',
  '此目录没有可用的脚本配置。': 'No available script configurations in this directory.',
  '读取失败，请核对脚本目录后重试。': 'Read failed. Check the script directory and retry.',
  '脚本根目录不存在，请选择有效目录后重试。': 'The script root directory does not exist. Choose an existing directory and retry.'
});

type Script = AutomationBundle['scripts'][number];
const props = defineProps<{ store: AutomationStore; modelValue: Script }>();
const emit = defineEmits<{ 'update:modelValue': [value: Script] }>();
const paths = computed(() => scriptPaths(props.store, props.modelValue));
const candidates = ref<readonly ScriptSourceCandidate[]>([]), busy = ref(false), note = ref('');
const accountPreview = shallowRef<ScriptSource['accountPreview']>();
const diagnosticDetails = shallowRef<ExtensionDetails>();
const message = computed(() => {
  const details = diagnosticDetails.value;
  return details?.diagnosticLocales[locale.value]?.[note.value] ||
    details?.diagnosticLocales[details.metadata?.defaultLocale || 'zh-CN']?.[note.value] || t(note.value);
});
let owner: AbortController | undefined;
const identity = () => [props.store.state.capabilities?.instanceId, props.modelValue.id,
  props.modelValue.extensionId, paths.value.root].join('\n');
const discoveryScope = () => JSON.stringify([identity(), props.store.state.events?.extensionGeneration,
  props.store.can('configuration.read'), props.store.can('automation.declare')]);
watch([identity, discoveryScope], ([key], [previous]) => {
  owner?.abort(); owner = undefined;
  candidates.value = []; busy.value = false; note.value = ''; diagnosticDetails.value = undefined;
  accountPreview.value = undefined;
  if (previous && previous !== key) {
    paths.value.executable = ''; paths.value.configuration = ''; paths.value.logs = ''; paths.value.arguments = '';
    props.store.drafts.delete(scriptSourceKey(props.modelValue.id));
    emit('update:modelValue', { ...props.modelValue, adapterResources: [] });
  }
  const extensionId = props.modelValue.extensionId;
  if (paths.value.root.trim() && extensionId && props.store.can('configuration.read')) void discover();
}, { immediate: true });
onBeforeUnmount(() => owner?.abort());
async function discover(): Promise<void> {
  owner?.abort(); const operation = new AbortController(); owner = operation;
  const key = discoveryScope(), root = paths.value.root, extensionId = props.modelValue.extensionId!;
  const current = () => owner === operation && !operation.signal.aborted && discoveryScope() === key;
  busy.value = true; note.value = '';
  try {
    const installed = await props.store.readEditorDirectory('extensions', extensionList, operation.signal);
    if (!current()) return;
    const extension = installed.items.find(item => item.extensionId === extensionId && item.isInstalled &&
      item.desiredEnabled && item.activationId && !item.unavailableReason);
    if (!extension) throw new Error('专项插件尚未就绪。');
    const operationId = createIdentity();
    const result = await props.store.client.query('/control/v1/automation/scripts/discover', {
      format: 'nexus.control.script-source', version: 1, operationId, extensionId,
      expectedInstalledRevision: extension.installedRevision, rootPath: root, purpose: 'edit'
    }, value => readSource(value, operationId, extensionId, root, extension.installedRevision), operation.signal);
    if (!current()) return;
    accountPreview.value = result.accountPreview;
    const details = !paths.value.executable && result.diagnostics?.length ? await props.store.client.read(
      '/control/v1/extensions/' + encodeURIComponent(extensionId) + '/details',
      value => extensionDetails(value, extensionId), operation.signal) : undefined;
    if (!current()) return;
    diagnosticDetails.value = details;
    candidates.value = result.candidates!;
    note.value = !paths.value.executable && !candidates.value.length && !result.editor
      ? result.diagnostics?.[0] || '此目录没有可用的脚本配置。' : '';
    if (!paths.value.executable && props.store.can('automation.declare')) {
      if (candidates.value.length) emit('update:modelValue', selectSource(props.store, props.modelValue, candidates.value[0]!));
      else if (result.editor) {
        paths.value.executable = result.editor.executable;
        emit('update:modelValue', { ...props.modelValue, adapterResources: [] });
      }
    }
  } catch (error) {
    if (current() && !paths.value.executable) {
      note.value = error instanceof Error && error.message === 'script_source_root_missing'
        ? '脚本根目录不存在，请选择有效目录后重试。'
        : error instanceof Error ? error.message : '读取失败，请核对脚本目录后重试。';
    }
  } finally { if (current()) { owner = undefined; busy.value = false; } }
}
</script>
<template>
  <nexus-toast v-if="note && !candidates.length && !busy" tone="warning" :message.prop="message" />
  <OkwwAccountPreview v-if="accountPreview" :preview="accountPreview" />
</template>
