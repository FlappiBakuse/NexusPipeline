<script setup lang="ts">
import { computed, ref, reactive } from 'vue';
import { useBindingSource } from './binding-source';
import type { ConfigurationStore } from '../app/store-ports';
import { t } from '../app/locale';
import { ControlFailure, objectValue } from '../control/client';
import { createIdentity } from '../control/identity';
import type { ConfigurationOverview } from './configuration-types';

const props = defineProps<{ store: ConfigurationStore; overview: ConfigurationOverview }>();
const source = reactive(useBindingSource(props.store, () => props.overview));
const emit = defineEmits<{ changed: [] }>();
const missing = computed(() => props.overview.resources.filter(item => !item.isSaved));
const busy = ref(false);
const uncertain = ref(false);
let pending: ReturnType<typeof selection> | null = null;
function selection() {
  return {
    format: 'nexus.control.configuration-import', version: 1, operationId: createIdentity(),
    bindingId: props.overview.bindingId, expectedBindingRevision: props.overview.bindingRevision,
    expectedScriptRevision: props.overview.scriptRevision,
    ...(source.required() ? { candidateId: source.selected, expectedInstalledRevision: source.revision } : {}),
    resources: missing.value.map(item => ({ resourceId: item.resourceId, expectedRevision: item.revision }))
  };
}
async function capture(): Promise<void> {
  if (busy.value || !props.store.can('configuration.edit') || source.required() && (!source.selected || source.busy)) return;
  pending ??= selection();
  busy.value = true;
  try {
    await props.store.client.write('/control/v1/configuration/import', pending, objectValue, pending.operationId);
    pending = null;
    uncertain.value = false;
    emit('changed');
  } catch (error) {
    uncertain.value = error instanceof ControlFailure && error.uncertain;
    if (!uncertain.value) pending = null;
    props.store.report(error);
  } finally { busy.value = false; }
}
</script>
<template>
  <section v-if="missing.length && store.can('configuration.edit')" class="config-import">
    <p>{{ t('读取脚本现有配置，为尚未保存的资源建立账号快照。脚本文件保持原样。') }}</p>
    <label v-if="source.required()">
      {{ t('选择脚本配置') }}
      <FormSelect v-model="source.selected" :disabled="source.busy || busy || uncertain" :aria-label="t('选择脚本配置')">
        <option disabled value="">{{ t('请选择') }}</option>
        <option v-for="candidate in source.candidates" :key="candidate.candidateId" :value="candidate.candidateId">{{ candidate.title }}</option>
      </FormSelect>
    </label>
    <p v-if="source.error" role="alert">{{ t(source.error) }} <button :disabled="source.busy || busy || uncertain" @click="source.discover">{{ t('重新检查') }}</button></p>
    <ul v-if="!source.required()"><li v-for="resource in missing" :key="resource.resourceId">{{ resource.label }}</li></ul>
    <p v-if="uncertain" role="status">{{ t('写入结果待确认，请先核实原操作。') }}</p>
    <button type="button" :disabled="busy || source.busy || (source.required() && !source.selected)" :aria-busy="busy || source.busy" @click="capture">
      {{ t(uncertain ? '重试配置读取' : '读取现有配置') }}
    </button>
  </section>
</template>
