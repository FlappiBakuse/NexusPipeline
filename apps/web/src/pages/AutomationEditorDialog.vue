<script setup lang="ts">
import { computed, nextTick, reactive, ref, toRefs, onMounted, onBeforeUnmount, watch, type ComputedRef } from 'vue';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import type { ExtensionState } from '../control/extensions';
import { metadataText } from '../control/extension-metadata';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationDraft } from '../app/automation-draft';
import type { AutomationBundle } from '../control/wire.generated';
import { AutomationEditor, type EntityKind, type AutomationSession } from './automation-editor';
import { initialValue, schemaAt } from '../control/schema';
import { createIdentity } from '../control/identity';
import { validateForm } from '../components/form-validation';
import BindingDetail from './BindingDetail.vue';
import BindingPolicyFields from './BindingPolicyFields.vue';
import QueueFields from './QueueFields.vue';
import AccountFields from './AccountFields.vue';
import AccountNameField from './AccountNameField.vue';
import ScriptFields from './ScriptFields.vue';
import ScriptActions from './ScriptActions.vue';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import { draftPresentation, draftDirective as vDraft } from '../app/draft-presentation';
import { t, locale } from '../app/locale';
import { providePathPicker } from './editor-path-picker';
const props = defineProps<{
  store: AutomationStore;
  kind: EntityKind;
  visible: boolean;
  model: AutomationEditor;
  session: AutomationSession;
  scriptType?: ExtensionState;
}>();
const emit = defineEmits<{
  close: [];
  import: [file: File];
  configure: [id: string];
  inspect: [];
  save: [form: HTMLElement | undefined];
}>();
const form = ref<HTMLElement>();
const modal = ref<HTMLElement>();
const bindingTitle = ref<HTMLElement>();
const {
  activeKind,
  selected,
  existing,
  userGlobal,
  draft,
  queueSchedules,
  bundle,
  accountBindings,
  createLabel
} = props.model;
providePathPicker(() => [props.model.instance, activeKind.value, selected.value, userGlobal.value],
  () => props.visible, error => props.store.report(error));
const { saving, error, notice, pending } = toRefs(props.session);
const presentation: ComputedRef<ReturnType<typeof draftPresentation>> = computed(() => draftPresentation(props.store.editors.target(
  props.store.state.capabilities?.instanceId || '', activeKind.value, selected.value).child('host-form'),
  existing.value, (): boolean => props.model.edited || presentation.value.state.changed || !!props.session.pending));
type BindingOverrides = NonNullable<AutomationBundle['users'][number]['bindingOverrides']>;
const defaultBindingOverrides = initialValue(schemaAt('automation-binding-policy.schema.json#/$defs/overrides')) as BindingOverrides;
const bindingOverrides = computed(() => (draft.value as AutomationBundle['users'][number]).bindingOverrides || defaultBindingOverrides);
const scriptUnavailable = computed(() => props.model.unavailable);
const specializedTitle = computed(() => props.scriptType && activeKind.value === 'scripts' ?
  (existing.value ? t('编辑脚本实例') : t('新建脚本实例')) + ' · ' +
  (props.scriptType.metadata ? metadataText(props.scriptType.metadata, locale.value).displayName :
    props.scriptType.artifactName || props.scriptType.extensionId) : '');

type Binding = AutomationBundle['bindings'][number];
type EditorHistory = { nexusAccountBinding?: { editorId: string; bindingId: string } };
const editorId = createIdentity();
const accountUrl = location.href;
const accountScreen = computed(() => !!bundle.value && activeKind.value === 'users' && existing.value && !userGlobal.value);
const bindingId = ref('');
const visitedBindings = reactive(new Set<string>());
const details = computed(() => accountScreen.value ? accountBindings.value.filter(binding => visitedBindings.has(binding.id)) : []);
const scripts = computed(() => new Map(bundle.value?.scripts.map(script => [script.id, script])));
const savedBindings = computed(() => new Set(bundle.value?.bindings.map(binding => binding.id)));
const selectedBinding = computed(() => details.value.find(binding => binding.id === bindingId.value));
const selectedName = computed(() => scripts.value.get(selectedBinding.value?.scriptId ?? '')?.name);
let parentScroll = 0;
let parentEntry = '';
let focusPending = false;
let returning = false;
let validateDetail = false;

function historicalBinding(state: EditorHistory | null = history.state): string {
  const marker = state?.nexusAccountBinding;
  return location.href === accountUrl && marker?.editorId === editorId ? marker.bindingId : '';
}
function changeBinding(id: string): void {
  if (id === bindingId.value) return;
  if (bindingId.value) {
    const focused = document.activeElement;
    const input = focused?.shadowRoot?.activeElement;
    if (focused?.localName === 'nexus-number-input' && form.value?.contains(focused) && input instanceof HTMLInputElement)
      input.dispatchEvent(new Event('change', { bubbles: true }));
  }
  if (id) {
    if (!bindingId.value) parentScroll = modal.value?.scrollTop ?? 0;
    visitedBindings.add(id);
    parentEntry = id;
  }
  bindingId.value = id;
  focusPending = true;
}
function openBinding(id: string, validate = false): void {
  if (!props.visible || location.href !== accountUrl || bindingId.value || returning) return;
  history.pushState({ ...history.state, nexusAccountBinding: { editorId, bindingId: id } }, '');
  validateDetail = validate;
  changeBinding(id);
}
function returnToAccount(): void {
  if (returning) return;
  if (historicalBinding()) {
    returning = true;
    history.back();
  } else changeBinding('');
}
function onAncestor(event: MouseEvent): void {
  if (event.button || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
  event.preventDefault();
  returnToAccount();
}
function restoreHistory(event: PopStateEvent): void {
  returning = false;
  if (!accountScreen.value || !props.model.editing.value || !props.model.current()) return;
  const id = historicalBinding(event.state);
  changeBinding(id && accountBindings.value.some(binding => binding.id === id) ? id : '');
}
function updateBinding(value: Binding): void {
  accountBindings.value = accountBindings.value.map(binding => binding.id === value.id ? value : binding);
}
function submitEditor(): void {
  if (bindingId.value) return;
  if (accountScreen.value) {
    const invalidField = form.value?.querySelector(
      '.binding-detail nexus-number-input[aria-invalid="true"]:not([inert])');
    const invalidId = invalidField?.closest<HTMLElement>('[data-draft-item]')?.dataset.draftItem ??
      accountBindings.value.find(binding => binding.maximumSuccessfulRunsPerDay === 0)?.id;
    if (invalidId) {
      if (validateForm(form.value)) openBinding(invalidId, true);
      return;
    }
  }
  emit('save', form.value);
}
watch([bindingId, () => props.visible], async () => {
  // The component root's v-show is applied in Vue's post-render effects.
  await nextTick();
  if (!focusPending || !props.visible || !modal.value || location.href !== accountUrl) return;
  focusPending = false;
  if (bindingId.value) {
    modal.value.scrollTop = 0;
    if (validateDetail) {
      validateDetail = false;
      if (!validateForm(form.value)) return;
      const limits = form.value?.querySelectorAll<UiElement<'nexus-number-input'>>(
        '.binding-detail:not([inert]) nexus-number-input:not([inert])');
      const invalid = limits && [...limits].find(input => input.max === 9999 && input.value === 0);
      if (invalid) {
        invalid.setAttribute('aria-invalid', 'true');
        error.value = '请修正无效字段后再保存。';
        invalid.focus();
        return;
      }
    }
    bindingTitle.value?.focus({ preventScroll: true });
  } else {
    modal.value.scrollTop = parentScroll;
    form.value?.querySelector<HTMLElement>(`[data-item-id="${CSS.escape(parentEntry)}"] .binding-entry`)
      ?.focus({ preventScroll: true });
  }
});
onMounted(() => window.addEventListener('popstate', restoreHistory));
onBeforeUnmount(() => {
  window.removeEventListener('popstate', restoreHistory);
  if (historicalBinding() && !returning) history.back();
  if (!presentation.value.modified) props.model.releasePristine();
});
</script>
<template>
  <nexus-modal
    ref="modal"
    :open.prop="visible"
    :size="activeKind === 'users' && !existing ? 'normal' : 'wide'"
    :title="selectedName || specializedTitle || (
      existing
        ? t(
            activeKind === 'users'
              ? userGlobal
                ? '全局管理'
                : '用户管理'
              : activeKind === 'queues'
                ? '编辑调度队列'
                : activeKind === 'scripts'
                  ? '编辑脚本实例'
                  : '脚本绑定'
          )
        : t(createLabel))
    "
    :locked.prop="saving"
    @nexus-close="emit('close')"
  >
    <nav v-if="bindingId" slot="title" class="editor-breadcrumb" :aria-label="t('页面路径')">
      <ol>
        <li><a :href="accountUrl" @click="onAncestor">{{ t('用户管理') }}</a></li>
        <li><h2 ref="bindingTitle" aria-current="page" tabindex="-1">{{ selectedName }}</h2></li>
      </ol>
    </nav>
    <ScriptActions
        slot="header-actions"
        v-if="activeKind === 'scripts'"
        :store="store"
        :script="draft as AutomationBundle['scripts'][number]"
        :existing="existing"
        :locked="saving || !!pending"
        @import="emit('import', $event)"
        @error="error = $event"
    />
    <div ref="form" v-draft="presentation" class="editor-pane">
      <template v-if="visible">
        <nexus-toast v-if="error" :tone="pending ? 'warning' : 'danger'" :message.prop="t(error)" />
        <nexus-toast v-else-if="notice" :message.prop="t(notice)" @nexus-close="notice = ''" />
        <nexus-toast v-else-if="pending && !saving" tone="warning" :message.prop="t('操作结果待确认；请查询状态，勿重复提交。')" />
      </template>
      <form novalidate :id="'automation-editor-' + kind" @submit.prevent="submitEditor">
        <fieldset :disabled="saving || !!pending" :inert="saving || !!pending" class="editor-fields">
          <QueueFields
            v-if="bundle && activeKind === 'queues'"
            :model-value="draft as AutomationBundle['queues'][number]"
            v-model:schedules="queueSchedules"
            :bundle="bundle"
            :store="store"
            @update:model-value="draft = $event as AutomationDraft"
            @discard-new-item="presentation.discardNewItem($event)"
          />
          <AccountNameField
            v-else-if="activeKind === 'users' && !existing"
            :model-value="String(draft.name ?? '')"
            @update:model-value="draft = { ...draft, name: $event }"
          />
          <template v-else-if="bundle && accountScreen">
            <AccountFields
              v-show="!bindingId"
              :inert="!!bindingId"
              :model-value="draft as AutomationBundle['users'][number]"
              :bundle="bundle"
              :bindings="accountBindings"
              @update:model-value="draft = $event as AutomationDraft"
              @update:bindings="accountBindings = $event"
              @open-binding="openBinding"
            />
            <BindingDetail v-for="binding in details" :key="binding.id" :binding="binding" :store="store"
              :script="scripts.get(binding.scriptId)" :account="draft as AutomationBundle['users'][number]"
              :saved="savedBindings.has(binding.id)" :visible="visible" :active="bindingId === binding.id"
              @update="updateBinding" @configure="emit('configure', $event)" />
          </template>
          <BindingPolicyFields
            v-else-if="bundle && activeKind === 'users'"
            :id="String(draft.id)"
            :store="store"
            global
            :model-value="bindingOverrides"
            @update:model-value="draft = { ...draft, bindingOverrides: $event as BindingOverrides }"
          />
          <ScriptFields
            v-else-if="activeKind === 'scripts'"
            :model-value="draft as AutomationBundle['scripts'][number]"
            :store="store"
            @update:model-value="draft = $event as AutomationDraft"
          />
          <ExtensionOutlet
            v-if="(activeKind !== 'users' || userGlobal) && (activeKind !== 'scripts' || existing)"
            :target="
              activeKind === 'scripts'
                ? 'automation.script.editor'
                : activeKind === 'users'
                  ? 'automation.global.editor'
                  : 'automation.queue.editor'
            "
            :store="store"
            :visible="visible"
            :entity-id="selected"
            :extension-ids="activeKind === 'scripts' ? (draft.kind === 'specialized' ? [String(draft.extensionId)] : []) : undefined"
          />
        </fieldset>
      </form>
    </div>
    <div slot="footer" class="editor-actions" :class="{ 'account-actions': activeKind === 'users' && existing }">
      <button v-if="bindingId" type="button" @click="returnToAccount">{{ t('返回上级') }}</button>
      <template v-else>
      <button v-if="pending" type="button"
        :disabled="saving || (!pending.definitionCommitted || pending.sourcePending) && !store.can('operations.read')" @click="emit('inspect')">
        {{ t(!pending.definitionCommitted ? '查询操作状态并刷新' : pending.sourcePending ? '继续处理所选配置' : '刷新已提交结果') }}
      </button>
      <button type="button" :disabled="saving || !!pending" @click="model.discard()">{{ t('取消') }}</button>
      <button
        class="primary"
        type="submit"
        :form="'automation-editor-' + kind"
        :disabled="saving || !!pending || scriptUnavailable || !store.can('automation.write')"
      >
        {{ t(saving ? '保存中…' : '保存') }}
      </button>
      </template>
    </div>
  </nexus-modal>
</template>
<style scoped>
.editor-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
  flex-wrap: wrap;
}
.editor-actions button {
  min-width: 80px;
}
.editor-actions button:not(.primary) {
  color: var(--muted);
}
.account-actions .primary {
  order: -1;
}
.editor-fields {
  border: 0;
  margin: 0;
  padding: 0;
  min-width: 0;
  box-shadow: none;
}
</style>
