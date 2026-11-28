<script setup lang="ts">
import { computed, ref } from 'vue';
import { t } from '../app/locale';
import { extensionIcon as scriptIcon } from '../control/extension-icons';
import { createIdentity } from '../control/identity';
import type { AutomationBundle } from '../control/wire.generated';
import AccountNameField from './AccountNameField.vue';
import BindingIdentity from './BindingIdentity.vue';

type User = AutomationBundle['users'][number];
type Binding = AutomationBundle['bindings'][number];
const props = defineProps<{
  modelValue: User;
  bindings: readonly Binding[];
  bundle: AutomationBundle;
}>();
const emit = defineEmits<{
  'update:modelValue': [value: User];
  'update:bindings': [value: Binding[]];
  openBinding: [id: string];
}>();
const removing = ref(false);
const choosing = ref(false);
const choices = ref<string[]>([]);
const scripts = computed(() => new Map(props.bundle.scripts.map(script => [script.id, script])));
const available = computed(() =>
  props.bundle.scripts.filter(item => !props.bindings.some(binding => binding.scriptId === item.id))
);
function openBinding(id: string): void {
  choosing.value = false;
  emit('openBinding', id);
}
function toggleChoice(id: string): void {
  choices.value = choices.value.includes(id)
    ? choices.value.filter(value => value !== id)
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
  if (removing.value) return;
  emit('update:bindings',
    ids.map((id, priority) => ({ ...props.bindings.find(item => item.id === id)!, priority })));
}
</script>

<template>
  <div class="account-fields">
    <AccountNameField :model-value="modelValue.name"
      @update:model-value="emit('update:modelValue', { ...modelValue, name: $event })" />
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
        <h3>{{ t('已绑定脚本实例') }}</h3>
        <button type="button" :class="{ active: removing }"
          @click="removing = !removing; choosing = false">
          {{ t(removing ? '完成编辑' : '编辑绑定') }}
        </button>
      </header>
      <button v-if="!removing && !choosing" type="button" class="add-binding"
        @click="choosing = true; choices = []">
        + {{ t('添加脚本') }}
      </button>
      <section v-if="choosing" class="script-picker">
        <header>
          <strong>{{ t('选择要绑定的脚本实例') }}</strong>
        </header>
        <div class="choice-grid">
          <button v-for="script in available" :key="script.id" type="button"
            :aria-pressed="choices.includes(script.id)" @click="toggleChoice(script.id)">
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
      <nexus-sortable-list class="binding-grid" :ids.prop="bindings.map(item => item.id)" :disabled.prop="removing"
        @nexus-reorder.stop="$event.target === $event.currentTarget && reorder($event.detail.ids)">
        <article v-for="binding in bindings" :key="binding.id" :data-item-id="binding.id" class="binding">
          <nexus-drag-handle :item-id="binding.id" :label="t('调整绑定顺序')" :disabled.prop="removing" />
          <template v-if="removing">
            <BindingIdentity :binding="binding" :script="scripts.get(binding.scriptId)" />
            <div class="entity-card-actions">
              <nexus-icon-button icon="trash" class="danger-action" :label="t('移除绑定')"
                @click="emit('update:bindings', bindings.filter(item => item.id !== binding.id))" />
            </div>
          </template>
          <template v-else>
            <button type="button" class="binding-entry" @click="openBinding(binding.id)">
              <BindingIdentity :binding="binding" :script="scripts.get(binding.scriptId)" />
              <span class="entity-card-actions" aria-hidden="true">
                <span class="entity-card-indicator"><nexus-icon name="chevronRight" /></span>
              </span>
            </button>
          </template>
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
label > nexus-text-area {
  flex-basis: 100%;
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
.add-binding {
  width: 100%;
  height: 120px;
  border-style: dashed;
}
.binding-grid,
.choice-grid {
  display: grid;
  align-items: start;
  margin-top: 16px;
}
.binding-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
.choice-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.binding {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px;
  min-width: 0;
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-md);
  background: var(--content-card);
  box-shadow: var(--nx-surface-shadow);
}
.binding-entry {
  display: flex;
  align-items: center;
  flex: 1;
  gap: 8px;
  border: 0;
  min-height: 0;
  padding: 0;
  background: transparent;
  min-width: 0;
  font: inherit;
  text-align: left;
  box-shadow: none;
}
.binding-entry:hover { background: transparent; color: var(--accent); }
.script-picker {
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-md);
  padding: 16px;
  box-shadow: var(--nx-surface-shadow);
}
.script-picker > nexus-empty-state {
  border: 0;
  background: transparent;
  box-shadow: none;
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
.binding-icon { width: 28px; height: 28px; flex: none; object-fit: contain; }
@media (max-width: 600px) {
  .binding-grid,
  .choice-grid { grid-template-columns: minmax(0, 1fr); }
}
</style>
