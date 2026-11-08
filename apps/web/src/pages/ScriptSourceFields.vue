<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle, ScriptSourceCandidate } from '../control/wire.generated';
import { extensionList, extensionDetails, type ExtensionDetails } from '../control/extensions';
import { createIdentity } from '../control/identity';
import { scriptPaths } from './script-paths';
import { readSource, selectSource, scriptSourceKey } from './script-source';
import { t, locale, registerMessages } from '../app/locale';
registerMessages({
  '请在 Host 本机创建专项脚本实例。': 'Create specialized script instances on the Host machine.',
  '读取脚本配置': 'Read script configurations', '选择脚本配置': 'Select script configuration',
  '请选择要使用的配置。': 'Select the configuration to use.',
  '此目录没有可用的脚本配置。': 'No available script configurations in this directory.',
  '读取失败，请核对脚本目录后重试。': 'Read failed. Check the script directory and retry.',
  '脚本根目录不存在，请选择有效目录后重试。': 'The script root directory does not exist. Choose an existing directory and retry.',
  '已选择配置，保存实例后生效。': 'Configuration selected. Save the instance to apply it.',
  '请在 Host 本机选择配置；当前连接只可查看候选。': 'Select configurations on the Host machine; this connection can only view candidates.'
});

type Script = AutomationBundle['scripts'][number];
const props = defineProps<{ store: AutomationStore; modelValue: Script }>();
const emit = defineEmits<{ 'update:modelValue': [value: Script] }>();
const paths = computed(() => scriptPaths(props.store, props.modelValue));
const candidates = ref<readonly ScriptSourceCandidate[]>([]), selected = ref(''), busy = ref(false), note = ref('');
const diagnostics = ref<readonly string[]>([]), details = ref<ExtensionDetails>();
const diagnosticTitle = (code: string): string => details.value?.diagnosticLocales[locale.value]?.[code]
  || details.value?.diagnosticLocales[details.value.metadata?.defaultLocale || 'zh-CN']?.[code] || code;
let owner: AbortController | undefined;
const identity = () => [props.store.state.capabilities?.instanceId, props.modelValue.id,
  props.modelValue.extensionId, paths.value.root].join('\n');
watch(identity, (_, previous) => {
  owner?.abort(); candidates.value = []; selected.value = ''; busy.value = false; note.value = '';
  diagnostics.value = []; details.value = undefined;
  if (previous) {
    paths.value.executable = ''; paths.value.configuration = ''; paths.value.logs = ''; paths.value.arguments = '';
    props.store.drafts.delete(scriptSourceKey(props.modelValue.id));
    emit('update:modelValue', { ...props.modelValue, adapterResources: [] });
  }
  if (paths.value.root.trim() && !paths.value.executable && props.modelValue.extensionId && props.store.can('configuration.read')) void discover();
}, { immediate: true });
onBeforeUnmount(() => owner?.abort());
async function discover(): Promise<void> {
  owner?.abort(); const operation = new AbortController(); owner = operation;
  const ticket = identity(), root = paths.value.root, extensionId = props.modelValue.extensionId!;
  const previousSelection = selected.value;
  busy.value = true; note.value = '';
  diagnostics.value = []; details.value = undefined;
  try {
    const installed = await props.store.client.read('/control/v1/extensions', extensionList, operation.signal);
    const extension = installed.items.find(item => item.extensionId === extensionId && item.isInstalled &&
      item.desiredEnabled && item.activationId && !item.unavailableReason);
    if (!extension) throw new Error('专项插件尚未就绪。');
    const operationId = createIdentity();
    const result = await props.store.client.query('/control/v1/automation/scripts/discover', {
      format: 'nexus.control.script-source', version: 1, operationId, extensionId,
      expectedInstalledRevision: extension.installedRevision, rootPath: root
    }, value => readSource(value, operationId, extensionId, root, extension.installedRevision), operation.signal);
    const dictionary = result.diagnostics?.length ? await props.store.client.read(
      '/control/v1/extensions/' + encodeURIComponent(extensionId) + '/details',
      value => extensionDetails(value, extensionId), operation.signal) : undefined;
    if (operation.signal.aborted || ticket !== identity()) return;
    diagnostics.value = result.diagnostics || []; details.value = dictionary;
    candidates.value = result.candidates!;
    selected.value = candidates.value.some(item => item.candidateId === previousSelection) ? previousSelection : '';
    note.value = selected.value ? '已选择配置，保存实例后生效。'
      : candidates.value.length ? '请选择要使用的配置。' : diagnostics.value.length ? '' : '此目录没有可用的脚本配置。';
    if (selected.value && props.store.can('automation.declare')) choose(selected.value);
    if (!paths.value.executable && candidates.value.length && props.store.can('automation.declare')) choose(candidates.value[0]!.candidateId);
  } catch (error) {
    if (!operation.signal.aborted && ticket === identity()) {
      note.value = error instanceof Error && error.message === 'script_source_root_missing'
        ? '脚本根目录不存在，请选择有效目录后重试。'
        : error instanceof Error ? error.message : '读取失败，请核对脚本目录后重试。';
    }
  } finally { if (owner === operation) busy.value = false; }
}
function choose(value: string): void {
  if (!props.store.can('automation.declare')) return;
  const candidate = candidates.value.find(item => item.candidateId === value);
  if (!candidate) return;
  selected.value = value; emit('update:modelValue', selectSource(props.store, props.modelValue, candidate));
  note.value = '已选择配置，保存实例后生效。';
}
</script>
<template>
  <nexus-toast v-if="note && !candidates.length && !busy" tone="warning" :message.prop="t(note)" />
</template>
