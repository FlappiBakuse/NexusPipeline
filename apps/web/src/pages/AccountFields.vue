<script setup lang="ts">
import { animateBindingLayout } from './binding-motion';
import { scriptIcon } from './script-icons';
import { createIdentity } from '../control/identity';
import { computed, ref } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle } from '../control/wire.generated';
import BindingPolicyFields from './BindingPolicyFields.vue';
import BindingTaskPlan from './BindingTaskPlan.vue';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import { t } from '../app/locale';

type User = AutomationBundle['users'][number];
type Binding = AutomationBundle['bindings'][number];
const props = defineProps<{
  modelValue: User;
  bindings: readonly Binding[];
  bundle: AutomationBundle;
  existing: boolean;
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
const scripts = computed(() => new Map(props.bundle.scripts.map(script => [script.id, script])));
const savedBindings = computed(() => new Set(props.bundle.bindings.map(binding => binding.id)));
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
</script>

<template>
  <div ref="root" class="account-fields">
    <label>
      {{ t('用户名') }}
      <em>*</em>
      <nexus-text-input
        :aria-label="t('用户名')"
        aria-required="true"
        :value.prop="modelValue.name"
        :max-length.prop.camel="120"
        @nexus-change="emit('update:modelValue', { ...modelValue, name: $event.detail.value })"
      />
    </label>
    <template v-if="existing">
      <label>
        {{ t('备注') }}
        <nexus-text-area
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
            <small>{{ t('每个绑定独立保存运行、通知和高级选项设置。') }}</small>
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
            <small>{{ t('可多选') }}</small>
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
          <p v-if="!available.length">{{ t('暂无可绑定的脚本实例') }}</p>
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
            :data-item-id="binding.id"
            :class="['binding', { expanded: expanded === binding.id }]"
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
                <span v-if="!removing" class="chevron">{{ expanded === binding.id ? '⌄' : '›' }}</span>
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
            <div class="binding-reveal" :class="{ open: expanded === binding.id }" :inert="expanded !== binding.id">
            <div class="binding-clip"><div v-if="visited.has(binding.id)" class="binding-content">
              <button
                class="edit-configuration"
                type="button"
                :disabled="!savedBindings.has(binding.id)"
                @click="emit('configure', binding.id)"
              >
                <strong>{{ t('编辑配置') }}</strong>
                <small>{{ t('启动主程序打开该脚本实例的用户配置') }}</small>
              </button>
              <BindingTaskPlan v-if="scripts.get(binding.scriptId)?.kind === 'specialized' && savedBindings.has(binding.id)"
                :store="store" :binding-id="binding.id" :visible="visible && expanded === binding.id" />

              <BindingPolicyFields
                :id="binding.id"
                :user-id="modelValue.id"
                :overrides="modelValue.bindingOverrides"
                :store="store"
                :model-value="binding"
                @update:model-value="update($event as Binding)"
              />
              <ExtensionOutlet
                target="automation.binding.editor"
                :store="store"
                :visible="visible && expanded === binding.id"
                :entity-id="binding.id"
                :extension-ids="scripts.get(binding.scriptId)?.extensionId ? [scripts.get(binding.scriptId)!.extensionId!] : []"
              />
            </div></div></div>
          </article>
        </nexus-sortable-list>
      </section>
    </template>
  </div>
</template>

<style scoped src="./account-fields.css"></style>
