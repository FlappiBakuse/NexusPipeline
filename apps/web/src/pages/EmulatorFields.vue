<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AutomationBundle, ControlEmulatorDiscover } from '../control/wire.generated';
import type { AppStore } from '../app/store';
import { objectValue } from '../control/client';
import { matchesSchema, schemaAt } from '../control/schema';
import { locale } from '../app/locale';
import { scriptPaths, scriptPathKey } from './script-paths';
type Script = AutomationBundle['scripts'][number];
type Selection = NonNullable<NonNullable<Script['target']>['android']>['emulator'];
interface Root { id: string; name: string; path: string; isDeclared: boolean }
interface Option { selection: NonNullable<Selection>; name: string; endpoint: string; adb: { rootId: string; relativePath: string } }
const props = defineProps<{ modelValue: Script; store: AppStore }>();
const emit = defineEmits<{ 'update:modelValue': [value: Script] }>();
const rootId = ref(props.modelValue.rootId), loading = ref(false), options = ref<Option[]>([]), roots = ref<Root[]>([]), diagnostics = ref<string[]>([]);
const english = computed(() => locale.value === 'en-US');
const text = (zh: string, en: string): string => english.value ? en : zh;
let ticket = 0;
watch(() => props.modelValue.id, () => { rootId.value = props.modelValue.rootId; clear(); });
watch(rootId, clear);
function clear(): void { ticket++; loading.value = false; options.value = []; roots.value = []; diagnostics.value = []; }
onBeforeUnmount(() => ticket++);
async function discover(): Promise<void> {
  const catalog = props.store.state.automation, script = props.modelValue.id;
  if (!catalog) return;
  const identity = props.store.state.capabilities?.instanceId, revision = catalog.catalogRevision, current = ++ticket;
  const body: ControlEmulatorDiscover = { format: 'nexus.control.emulator-discover', version: 1, scriptId: script,
    expectedCatalogRevision: revision, rootIds: [rootId.value] };
  if (!matchesSchema(body, schemaAt('control-emulator-discover.schema.json'))) throw new Error('emulator_request_invalid');
  loading.value = true;
  try {
    const response = await props.store.client.query('/control/v1/automation/emulators/discover', body, objectValue);
    if (ticket !== current || props.modelValue.id !== script || props.store.state.capabilities?.instanceId !== identity ||
      props.store.state.automation?.catalogRevision !== revision) return;
    if (response.catalogRevision !== revision || !Array.isArray(response.roots) || !Array.isArray(response.targets) || !Array.isArray(response.diagnostics))
      throw new Error('emulator_response_invalid');
    roots.value = response.roots.map(value => {
      const root = objectValue(value);
      if (typeof root.id !== 'string' || typeof root.name !== 'string' || typeof root.path !== 'string' || typeof root.isDeclared !== 'boolean') throw new Error('emulator_response_invalid');
      return root as unknown as Root;
    });
    options.value = response.targets.map(value => {
      const option = objectValue(value), adb = objectValue(option.adb);
      if (!matchesSchema(option.selection, schemaAt('emulator-target.schema.json#/$defs/selection')) || typeof option.name !== 'string' ||
        typeof option.endpoint !== 'string' || typeof adb.rootId !== 'string' || typeof adb.relativePath !== 'string' ||
        !roots.value.some(root => root.id === adb.rootId)) throw new Error('emulator_response_invalid');
      return option as unknown as Option;
    });
    diagnostics.value = response.diagnostics.map(value => String(objectValue(value).code));
    if (!options.value.length) diagnostics.value.push('emulator_no_bindable_instances');
  } catch (reason) { props.store.report(reason); }
  finally { if (ticket === current) loading.value = false; }
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
      <FormSelect :value="rootId" @change="rootId = ($event.target as HTMLSelectElement).value"><option v-for="root in store.state.automation?.bundle.roots" :key="root.id" :value="root.id">{{ root.name }}</option></FormSelect>
    </label>
    <button type="button" :disabled="loading || !store.state.automation?.bundle.scripts.some(script => script.id === modelValue.id)" @click="discover">
      {{ text('发现模拟器实例', 'Discover emulator instances') }}
    </button>
    <p v-if="!store.state.automation?.bundle.scripts.some(script => script.id === modelValue.id)">{{ text('请先保存脚本，再发现实例；也可填写手动 ADB 路径。', 'Save the script before discovery, or enter a manual ADB path.') }}</p>
    <label>{{ text('实例与 ADB 地址', 'Instance and ADB endpoint') }}
      <FormSelect :value="''" @change="select(($event.target as HTMLSelectElement).value)">
        <option value="">{{ text('使用手动 ADB', 'Use manual ADB') }}</option>
        <option v-for="(option, index) in options" :key="option.selection.targetId + option.endpoint" :value="String(index)">{{ option.selection.vendor }} · {{ option.name }} · {{ option.endpoint }}</option>
      </FormSelect>
    </label>
    <p v-if="modelValue.target?.android?.emulator">{{ modelValue.target.android.emulator.vendor }} · {{ modelValue.target.android.endpoint }}</p>
    <p v-for="(message, index) in diagnostics" :key="index" role="status">{{ message === 'emulator_no_bindable_instances' ? text('未发现可绑定实例。', 'No bindable instances found.') : message }}</p>
  </div>
</template>
