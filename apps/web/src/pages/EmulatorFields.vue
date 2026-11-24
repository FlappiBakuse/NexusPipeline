<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AutomationBundle, ControlEmulatorDiscover } from '../control/wire.generated';
import type { AutomationStore } from '../app/store-ports';
import { objectValue } from '../control/client';
import { matchesSchema, schemaAt } from '../control/schema';
import { locale } from '../app/locale';
import { scriptPaths, scriptPathKey } from './script-paths';
type Script = AutomationBundle['scripts'][number];
type Selection = NonNullable<NonNullable<Script['target']>['android']>['emulator'];
interface Root { id: string; name: string; path: string; isDeclared: boolean }
interface Option { selection: NonNullable<Selection>; name: string; endpoint: string; adb: { rootId: string; relativePath: string } }
const props = defineProps<{ modelValue: Script; store: AutomationStore }>();
const emit = defineEmits<{ 'update:modelValue': [value: Script] }>();
const rootId = ref(props.modelValue.rootId), loading = ref(false), options = ref<Option[]>([]), roots = ref<Root[]>([]), diagnostics = ref<string[]>([]);
const english = computed(() => locale.value === 'en-US');
const text = (zh: string, en: string): string => english.value ? en : zh;
const stored = computed(() => props.store.state.automation?.bundle.scripts.some(script => script.id === props.modelValue.id));
const canDiscover = computed(() => props.store.can('automation.read'));
let owner: AbortController | undefined;
const scope = () => JSON.stringify([props.store.state.capabilities?.instanceId,
  props.store.state.automation?.catalogRevision, props.store.state.events?.extensionGeneration,
  props.modelValue.id, rootId.value, canDiscover.value]);
watch(() => props.modelValue.id, () => { rootId.value = props.modelValue.rootId; });
watch(scope, () => {
  owner?.abort(); owner = undefined;
  loading.value = false; options.value = []; roots.value = []; diagnostics.value = [];
});
onBeforeUnmount(() => owner?.abort());
async function discover(): Promise<void> {
  const catalog = props.store.state.automation, script = props.modelValue.id;
  if (!catalog || !canDiscover.value) return;
  owner?.abort();
  const operation = new AbortController(); owner = operation;
  const key = scope(), revision = catalog.catalogRevision;
  const current = () => owner === operation && !operation.signal.aborted && scope() === key;
  const body: ControlEmulatorDiscover = { format: 'nexus.control.emulator-discover', version: 1, scriptId: script,
    expectedCatalogRevision: revision, rootIds: [rootId.value] };
  loading.value = true;
  diagnostics.value = [];
  try {
    if (!matchesSchema(body, schemaAt('control-emulator-discover.schema.json'))) throw new Error('emulator_request_invalid');
    const response = await props.store.client.query('/control/v1/automation/emulators/discover', body, objectValue, operation.signal);
    if (!current()) return;
    if (response.catalogRevision !== revision || !Array.isArray(response.roots) || !Array.isArray(response.targets) || !Array.isArray(response.diagnostics))
      throw new Error('emulator_response_invalid');
    const foundRoots = response.roots.map(value => {
      const root = objectValue(value);
      if (typeof root.id !== 'string' || typeof root.name !== 'string' || typeof root.path !== 'string' || typeof root.isDeclared !== 'boolean') throw new Error('emulator_response_invalid');
      return root as unknown as Root;
    });
    const foundOptions = response.targets.map(value => {
      const option = objectValue(value), adb = objectValue(option.adb);
      if (!matchesSchema(option.selection, schemaAt('emulator-target.schema.json#/$defs/selection')) || typeof option.name !== 'string' ||
        typeof option.endpoint !== 'string' || typeof adb.rootId !== 'string' || typeof adb.relativePath !== 'string' ||
        !foundRoots.some(root => root.id === adb.rootId)) throw new Error('emulator_response_invalid');
      return option as unknown as Option;
    });
    const foundDiagnostics = response.diagnostics.map(value => String(objectValue(value).code));
    roots.value = foundRoots; options.value = foundOptions; diagnostics.value = foundDiagnostics;
    if (!options.value.length) diagnostics.value.push('emulator_no_bindable_instances');
  } catch (reason) { if (current()) props.store.report(reason); }
  finally { if (current()) { owner = undefined; loading.value = false; } }
}
function select(value: string): void {
  const android = props.modelValue.target?.android;
  if (!android || !props.modelValue.target) return;
  if (!value) {
    const { emulator: omitted, ...plain } = android;
    emit('update:modelValue', { ...props.modelValue, target: { ...props.modelValue.target, android: plain } }); return;
  }
  const option = options.value[Number(value)], root = roots.value.find(root => root.id === option?.adb.rootId);
  if (!option || !root) return;
  const paths = scriptPaths(props.store, props.modelValue);
  paths.androidTool = root.path.replace(/[\\/]$/, '') + '\\' + option.adb.relativePath.replace(/\//g, '\\');
  paths.emulatorRoot = root;
  props.store.dirty.add(scriptPathKey(props.modelValue.id));
  emit('update:modelValue', { ...props.modelValue, target: { ...props.modelValue.target,
    android: { ...android, driver: 'genericAdb', tool: { rootId: root.id, relative: option.adb.relativePath }, endpoint: option.endpoint, emulator: option.selection } } });
}
</script>

<template>
  <div class="emulator-fields">
    <label>{{ text('便携版搜索根目录', 'Portable search root') }}
      <FormSelect
        data-draft-field="EmulatorFields/rootId" :value="rootId" @change="rootId = ($event.target as HTMLSelectElement).value"><option v-for="root in store.state.automation?.bundle.roots" :key="root.id" :value="root.id">{{ root.name }}</option></FormSelect>
    </label>
    <button type="button" :disabled="loading || !stored || !canDiscover" @click="discover">
      {{ text('发现模拟器实例', 'Discover emulator instances') }}
    </button>
    <p v-if="!stored">{{ text('请先保存脚本，再发现实例；也可填写手动 ADB 路径。', 'Save the script before discovery, or enter a manual ADB path.') }}</p>
    <label>{{ text('实例与 ADB 地址', 'Instance and ADB endpoint') }}
      <FormSelect
        data-draft-field="EmulatorFields/''" :value="''" @change="select(($event.target as HTMLSelectElement).value)">
        <option value="">{{ text('使用手动 ADB', 'Use manual ADB') }}</option>
        <option v-for="(option, index) in options" :key="option.selection.targetId + option.endpoint" :value="String(index)">{{ option.selection.vendor }} · {{ option.name }} · {{ option.endpoint }}</option>
      </FormSelect>
    </label>
    <p v-if="modelValue.target?.android?.emulator">{{ modelValue.target.android.emulator.vendor }} · {{ modelValue.target.android.endpoint }}</p>
    <nexus-toast v-if="diagnostics.length" tone="warning" :message.prop="diagnostics.map(message =>
      message === 'emulator_no_bindable_instances' ? text('未发现可绑定实例。', 'No bindable instances found.') : message).join(' · ')" />
  </div>
</template>
