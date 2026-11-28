<script setup lang="ts">
import { computed, ref } from 'vue';
import { t } from '../app/locale';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle } from '../control/wire.generated';
import { accountKey, discardBindingCandidate, type AccountDraft } from './account-draft';
import BindingIdentity from './BindingIdentity.vue';
import BindingPolicyFields from './BindingPolicyFields.vue';
import BindingTaskPlan from './BindingTaskPlan.vue';

type Binding = AutomationBundle['bindings'][number];
const props = defineProps<{
  binding: Binding;
  script?: AutomationBundle['scripts'][number];
  account: AutomationBundle['users'][number];
  saved: boolean;
  active: boolean;
  visible: boolean;
  store: AutomationStore;
}>();
const emit = defineEmits<{
  update: [binding: Binding];
  configure: [id: string];
}>();
const discardBlocked = ref(false);
const candidate = computed(() =>
  (props.store.drafts.get(accountKey(props.store, props.account.id)) as AccountDraft | undefined)?.candidates[props.binding.id]);
const taskDraft = computed(() => {
  const binding = props.binding;
  const revision = props.store.state.automation?.revisions.find(item => item.kind === 'script' && item.id === binding.scriptId)?.revision;
  return { userId: binding.userId, scriptId: binding.scriptId, scriptRevision: revision || 0, candidateId: candidate.value };
});
function discardConfiguration(): void {
  const { store, binding } = props;
  const instance = store.state.capabilities?.instanceId || '';
  discardBlocked.value = !store.editors.target(instance, 'bindings', binding.id).canCancel();
  if (discardBlocked.value) return;
  store.editors.settle(instance, 'bindings', binding.id, 'cancel');
  discardBindingCandidate(store, binding.userId, binding.id);
}
</script>

<template>
  <article v-show="active" :inert="!active" class="binding-detail" :data-draft-item="binding.id">
    <header><BindingIdentity :binding="binding" :script="script" /></header>
    <button type="button" class="edit-configuration" :disabled="!store.can('configuration.edit')"
      @click="emit('configure', binding.id)">
      <strong>{{ t('编辑配置') }}</strong>
      <small>{{ t('启动主程序打开该脚本实例的用户配置') }}</small>
    </button>
    <button v-if="candidate" type="button" @click="discardConfiguration">
      {{ t('放弃配置草稿') }}
    </button>
    <p v-if="discardBlocked && active" role="status">{{ t('请先结束当前配置编辑，再放弃草稿。') }}</p>
    <BindingTaskPlan v-if="script?.kind === 'specialized' && (saved || candidate && script.extensionId !== 'maa-framework')"
      :store="store" :binding-id="binding.id" :visible="visible && active"
      :draft="saved ? undefined : taskDraft" />
    <BindingPolicyFields :id="binding.id" :user-id="account.id" :overrides="account.bindingOverrides"
      :store="store" :model-value="binding" @update:model-value="emit('update', $event as Binding)" />
  </article>
</template>

<style scoped>
.binding-detail {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 20px;
  min-width: 0;
}
.binding-detail header { display: flex; align-items: center; }
.edit-configuration {
  display: grid;
  place-content: center;
  gap: 4px;
  min-height: 120px;
  background: var(--content-control);
  width: 100%;
  min-width: 0;
  box-sizing: border-box;
}
small {
  color: var(--muted);
  font-size: 12px;
}
</style>
