<script setup lang="ts">
import { animateBindingLayout } from './binding-motion';
import { scriptIcon } from './script-icons';
import { createIdentity } from '../control/identity';
import { computed, ref } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle } from '../control/wire.generated';
import BindingPolicyFields from './BindingPolicyFields.vue';
import BindingTaskPlan from './BindingTaskPlan.vue';
import { t } from '../app/locale';
import { accountKey, discardBindingCandidate, type AccountDraft } from './account-draft';

type User = AutomationBundle['users'][number];
type Binding = AutomationBundle['bindings'][number];
const props = defineProps<{
  modelValue: User;
  bindings: readonly Binding[];
  bundle: AutomationBundle;
  store: AutomationStore;
  visible: boolean;
}>();
const emit = defineEmits<{
  'update:modelValue': [value: User];
  'update:bindings': [value: Binding[]];
  configure: [id: string];
}>();
const root = ref<HTMLElement>();
const removing = ref(false);
const choosing = ref(false);
const expanded = ref('');
const visited = ref(new Set<string>());
const choices = ref<string[]>([]);
const discardError = ref('');
const scripts = computed(() => new Map(props.bundle.scripts.map(script => [script.id, script])));
const savedBindings = computed(() => new Set(props.bundle.bindings.map(binding => binding.id)));
const candidates = computed(() => (props.store.drafts.get(accountKey(props.store, props.modelValue.id)) as AccountDraft | undefined)?.candidates || {});
function taskDraft(binding: Binding) {
  const revision = props.store.state.automation?.revisions.find(item => item.kind === 'script' && item.id === binding.scriptId)?.revision;
  return { userId: binding.userId, scriptId: binding.scriptId, scriptRevision: revision || 0, candidateId: candidates.value[binding.id] };
}
const available = computed(() =>
  props.bundle.scripts.filter((item) => !props.bindings.some((binding) => binding.scriptId === item.id))
);
function toggleChoice(id: string): void {
  choices.value = choices.value.includes(id)
    ? choices.value.filter((value) => value !== id)
    : [...choices.value, id];
}
function add(): void {
  const added = choices.value.map((scriptId, index) => ({
    id: createIdentity(),
    userId: props.modelValue.id,
    scriptId,
    enabled: true,
    priority: props.bindings.length + index,
    runDays: -1,
    maximumSuccessfulRunsPerDay: -1,
    notifyEnabled: true,
    notificationTaskIds: []
  }));
  emit('update:bindings', [...props.bindings, ...added]);
  choosing.value = false;
  choices.value = [];
}
function reorder(ids: string[]): void {
  if (removing.value || expanded.value) return;
  emit(
    'update:bindings',
    ids.map((id, priority) => ({ ...props.bindings.find((item) => item.id === id)!, priority }))
  );
}
function update(value: Binding): void {
  emit(
    'update:bindings',
    props.bindings.map((item) => (item.id === value.id ? value : item))
  );
}
function discardConfiguration(binding: Binding): void {
  const instance = props.store.state.capabilities?.instanceId || '';
  const scope = props.store.editors.target(instance, 'bindings', binding.id);
  discardError.value = scope.canCancel() ? '' : binding.id;
  if (discardError.value) return;
  props.store.editors.settle(instance, 'bindings', binding.id, 'cancel');
  discardBindingCandidate(props.store, binding.userId, binding.id);
}
</script>

<template>
  <div ref="root" class="account-fields">
    <label>
      {{ t('用户名') }}
      <em>*</em>
      <nexus-text-input
        data-draft-field="AccountFields/modelValue.name"
        :aria-label="t('用户名')"
        aria-required="true"
        :value.prop="modelValue.name"
        :max-length.prop.camel="120"
        @nexus-change="emit('update:modelValue', { ...modelValue, name: $event.detail.value })"
      />
    </label>
    <label>
      {{ t('备注') }}
      <nexus-text-area
      data-draft-field="AccountFields/modelValue.notes || ''"
        :aria-label="t('备注')"
        :rows.prop="4"
        :max-length.prop.camel="4000"
        :value.prop="modelValue.notes || ''"
        @nexus-change="emit('update:modelValue', { ...modelValue, notes: $event.detail.value })"
      />
    </label>
    <section class="bindings-section">
      <header>
        <div>
          <h3>{{ t('已绑定脚本实例') }}</h3>
        </div>
        <button
          v-if="!expanded"
          type="button"
          :class="{ active: removing }"
          @click="
            removing = !removing;
            choosing = false;
          "
        >
          {{ t(removing ? '完成编辑' : '编辑绑定') }}
        </button>
      </header>
      <button
        v-if="!removing && !choosing && !expanded"
        type="button"
        class="add-binding"
        @click="
          choosing = true;
          choices = [];
        "
      >
        + {{ t('添加脚本') }}
      </button>
      <section v-if="choosing" class="script-picker">
        <header>
          <strong>{{ t('选择要绑定的脚本实例') }}</strong>
        </header>
        <div class="choice-grid">
          <button
            v-for="script in available"
            :key="script.id"
            type="button"
            :aria-pressed="choices.includes(script.id)"
            @click="toggleChoice(script.id)"
          >
            <img class="binding-icon" :src="scriptIcon(script.extensionId)" alt="" />
            {{ script.name }}
          </button>
        </div>
        <nexus-empty-state v-if="!available.length" :title="t('暂无可绑定的脚本实例')" />
        <div class="picker-actions">
          <button type="button" @click="choosing = false">{{ t('取消') }}</button>
          <button type="button" class="primary" :disabled="!choices.length" @click="add">
            {{ t('确定') }}
          </button>
        </div>
      </section>
      <nexus-sortable-list
        class="binding-grid"
        :ids.prop="bindings.map((item) => item.id)"
        :disabled.prop="removing || !!expanded"
        @nexus-reorder.stop="$event.target === $event.currentTarget && reorder($event.detail.ids)"
      >
        <article
          v-for="binding in bindings"
          :key="binding.id"
          :data-item-id="binding.id" :data-draft-item="binding.id"
          :class="['binding', { expanded: expanded === binding.id }]" data-surface="collapsible"
        >
          <div class="binding-heading">
            <nexus-drag-handle
              v-if="!removing && !expanded"
              :item-id="binding.id"
              :label="t('调整绑定顺序')"
            />
            <button
              type="button"
              class="binding-toggle"
              :disabled="removing"
              :aria-expanded="expanded === binding.id"
              @click="animateBindingLayout(root, () => { expanded = expanded === binding.id ? '' : binding.id; choosing = false; visited.add(binding.id); })"
            >
              <img class="binding-icon" :src="scriptIcon(scripts.get(binding.scriptId)?.extensionId)" alt="" />
              <span>
                <strong>{{ scripts.get(binding.scriptId)?.name }}</strong>
                <span class="badges">
                  <nexus-badge :tone="binding.enabled ? 'success' : 'neutral'" :label="t(binding.enabled ? '已启用' : '已停用')" />
                  <nexus-badge
                    :label="binding.runDays === -1 ? t('永久运行') : t('运行天数') + ' ' + binding.runDays"
                  />
                </span>
              </span>
              <nexus-icon v-if="!removing" class="disclosure-chevron" aria-hidden="true"
                :name="expanded === binding.id ? 'chevronDown' : 'chevronRight'" />
            </button>
            <button
              v-if="removing"
              type="button"
              class="danger"
              @click="
                emit(
                  'update:bindings',
                  bindings.filter((item) => item.id !== binding.id)
                )
              "
            >
              {{ t('移除绑定') }}
            </button>
          </div>
          <div class="binding-reveal disclosure-reveal" :data-expanded="expanded === binding.id" :inert="expanded !== binding.id">
          <div class="disclosure-clip"><div v-if="visited.has(binding.id)" class="binding-content">
            <button
              class="edit-configuration"
              type="button"
              :disabled="!store.can('configuration.edit')"
              @click="emit('configure', binding.id)"
            >
              <strong>{{ t('编辑配置') }}</strong>
              <small>{{ t('启动主程序打开该脚本实例的用户配置') }}</small>
            </button>
            <button v-if="candidates[binding.id]" type="button" @click="discardConfiguration(binding)">
              {{ t('放弃配置草稿') }}
            </button>
            <p v-if="discardError === binding.id && expanded === binding.id" role="status">{{ t('请先结束当前配置编辑，再放弃草稿。') }}</p>
            <BindingTaskPlan v-if="scripts.get(binding.scriptId)?.kind === 'specialized' &&
              (savedBindings.has(binding.id) || candidates[binding.id] && scripts.get(binding.scriptId)?.extensionId !== 'maa-framework')"
              :store="store" :binding-id="binding.id" :visible="visible && expanded === binding.id"
              :draft="savedBindings.has(binding.id) ? undefined : taskDraft(binding)" />

            <BindingPolicyFields
              :id="binding.id"
              :user-id="modelValue.id"
              :overrides="modelValue.bindingOverrides"
              :store="store"
              :model-value="binding"
              @update:model-value="update($event as Binding)"
            />
          </div></div></div>
        </article>
      </nexus-sortable-list>
    </section>
  </div>
</template>

<style scoped>
.account-fields {
  display: grid;
  gap: 20px;
  min-width: 0;
}
label {
  display: flex;
  flex-wrap: wrap;
  gap: 7px 2px;
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}
label > nexus-text-input,
label > nexus-text-area {
  flex-basis: 100%;
}
em {
  color: var(--bad);
  font-style: normal;
}
.bindings-section {
  border-top: 1px solid var(--border);
  padding-top: 20px;
}
header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}
header > button { flex: none; white-space: nowrap; }
h3 {
  font-size: 14px;
  margin: 0 0 3px;
}
small {
  color: var(--muted);
  font-size: 12px;
}
.add-binding {
  width: 100%;
  height: 120px;
  border-style: dashed;
}
.binding-grid,
.choice-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
  align-items: start;
  margin-top: 16px;
}
.binding {
  min-width: 0;
  border: 1px solid var(--content-card-border, var(--border));
  border-radius: 10px;
  overflow: hidden;
}
.binding.expanded {
  grid-column: 1 / -1;
  order: -1;
}
.binding-heading {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px;
  min-height: 58px;
}
.binding-toggle {
  display: flex;
  align-items: center;
  flex: 1;
  gap: 12px;
  border: 0;
  padding: 0;
  background: transparent;
  min-width: 0;
  text-align: left;
}
.binding-toggle > span:first-of-type {
  display: grid;
  gap: 4px;
  flex: 1;
  min-width: 0;
}
.binding-toggle strong {
  font-size: 13px;
  overflow-wrap: anywhere;
}
.binding-toggle:hover { background: transparent; }
.choice-grid nexus-icon {
  display: grid;
  place-content: center;
  width: 36px;
  height: 36px;
  border: 1px solid var(--border);
  border-radius: 6px;
}
.badges {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.binding-toggle .disclosure-chevron {
  margin-inline-end: 8px;
}
.binding-content {
  display: grid;
  gap: 16px;
  padding: 16px;
  border-top: 1px solid var(--border);
  min-width: 0;
  grid-template-columns: minmax(0, 1fr);
}
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
.script-picker {
  border: 1px solid var(--border);
  border-radius: 16px;
  padding: 16px;
}
.choice-grid button {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 12px;
  text-align: left;
}
[aria-pressed='true'],
.active {
  border-color: var(--accent);
  color: var(--accent);
  background: var(--accent-soft);
}
.picker-actions {
  display: flex;
  justify-content: end;
  gap: 8px;
  margin-top: 12px;
}
@media (max-width: 600px) {
  .binding-grid,
  .choice-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}

.binding-icon { width: 28px; height: 28px; flex: none; object-fit: contain; }

.binding.expanded { border-color: var(--content-control-border); }
</style>
