<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { configurationOverview, type ConfigurationOverview } from './configuration-types';
import AdapterEditorPanel from './AdapterEditorPanel.vue';
import { t, registerMessages } from '../app/locale';
registerMessages({
  任务计划: 'Task plan',
  '配置不可用，请先保存用户配置快照。': 'Configuration is unavailable. Save the account configuration first.',
  '当前权限无法读取任务计划。': 'The current permissions do not allow reading the task plan.',
  重新检查: 'Check again'
});
const props = defineProps<{ store: AppStore; bindingId: string; visible: boolean }>();
const overview = ref<ConfigurationOverview>();
const error = ref('');
let owner: AbortController | undefined;
async function read(): Promise<void> {
  owner?.abort(); overview.value = undefined; error.value = '';
  if (!props.visible) return;
  if (!props.store.can('configuration.read')) { error.value = '当前权限无法读取任务计划。'; return; }
  const current = new AbortController(); owner = current;
  const id = props.bindingId, instance = props.store.state.capabilities?.instanceId;
  try {
    const result = await props.store.client.read('/control/v1/configuration/bindings/' + encodeURIComponent(id) + '/resources',
      configurationOverview, current.signal);
    current.signal.throwIfAborted();
    if (result.bindingId !== id || result.kind !== 'specialized' || instance !== props.store.state.capabilities?.instanceId)
      throw new TypeError('configuration_owner_changed');
    overview.value = result;
  } catch (reason) {
    if (!current.signal.aborted) error.value = reason instanceof Error ? reason.message : '配置不可用，请先保存用户配置快照。';
  }
}
watch(() => [props.visible, props.bindingId, props.store.state.automation?.catalogRevision], read, { immediate: true });
onBeforeUnmount(() => owner?.abort());
</script>
<template>
  <section class="binding-task-plan">
    <header><h3>{{ t('任务计划') }}</h3><button type="button" @click="read">{{ t('重新检查') }}</button></header>
    <p v-if="error" role="alert">{{ t(error) }}</p>
    <AdapterEditorPanel v-if="overview?.resources.some(item => item.isSaved)" :store="store" :overview="overview" :visible="visible" @changed="read" />
    <p v-else-if="overview">{{ t('配置不可用，请先保存用户配置快照。') }}</p>
  </section>
</template>
<style scoped>
.binding-task-plan { display: grid; gap: 12px; min-width: 0; }
header { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
h3, p { margin: 0; }
p { color: var(--muted); }
</style>
