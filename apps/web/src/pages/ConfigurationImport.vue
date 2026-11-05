<script setup lang="ts">
import { computed, ref } from 'vue';
import type { AppStore } from '../app/store';
import { t } from '../app/locale';
import { ControlFailure, objectValue } from '../control/client';
import { createIdentity } from '../control/identity';
import type { ConfigurationOverview } from './configuration-types';

const props = defineProps<{ store: AppStore; overview: ConfigurationOverview }>();
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
    resources: missing.value.map(item => ({ resourceId: item.resourceId, expectedRevision: item.revision }))
  };
}
async function capture(): Promise<void> {
  if (busy.value || !props.store.can('configuration.edit')) return;
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
    <ul><li v-for="resource in missing" :key="resource.resourceId">{{ resource.label }}</li></ul>
    <p v-if="uncertain" role="status">{{ t('写入结果待确认，请先核实原操作。') }}</p>
    <button :disabled="busy" :aria-busy="busy" @click="capture">
      {{ t(uncertain ? '重试配置读取' : '读取现有配置') }}
    </button>
  </section>
</template>
