<script setup lang="ts">
import { computed, ref, toRefs, watch, onBeforeUnmount, type ComputedRef } from 'vue';
import { extensionList } from '../control/extensions';
import { metadataText, type ExtensionMetadata } from '../control/extension-metadata';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationDraft } from '../app/automation-draft';
import type { AutomationBundle } from '../control/wire.generated';
import { AutomationEditor, type EntityKind } from './automation-editor';
import type { AutomationSession } from './automation-session';
import AutomationRelationEditor from './AutomationRelationEditor.vue';
import QueueFields from './QueueFields.vue';
import AccountFields from './AccountFields.vue';
import ScriptFields from './ScriptFields.vue';
import ScriptActions from './ScriptActions.vue';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import DraftNotice from '../components/DraftNotice.vue';
import { draftPresentation, draftDirective as vDraft } from '../app/draft-presentation';
import { t, locale } from '../app/locale';
const props = defineProps<{
  store: AutomationStore;
  kind: EntityKind;
  visible: boolean;
  model: AutomationEditor;
  session: AutomationSession;
}>();
const emit = defineEmits<{
  close: [];
  import: [file: File];
  configure: [id: string];
  inspect: [];
  save: [form: HTMLElement | undefined];
}>();
const form = ref<HTMLElement>();
const {
  activeKind,
  selected,
  userGlobal,
  draft,
  queueSchedules,
  bundle,
  rows,
  accountBindings,
  createLabel
} = props.model;
const { saving, error, notice, pending } = toRefs(props.session);
const presentation: ComputedRef<ReturnType<typeof draftPresentation>> = computed(() => draftPresentation(props.store.editors.target(
  props.store.state.capabilities?.instanceId || '', activeKind.value, selected.value).child('host-form'),
  rows.value.some(row => row.id === selected.value), (): boolean => props.model.edited || presentation.value.state.changed || !!props.session.pending));
const scriptUnavailable = computed(() => props.model.unavailable);
const typeMetadata = ref<ExtensionMetadata>();
let typeOwner: AbortController | undefined;
watch(() => [props.visible, activeKind.value, draft.value.extensionId] as const, async ([visible, kind, id]) => {
  typeOwner?.abort(); typeMetadata.value = undefined;
  if (!visible || kind !== 'scripts' || typeof id !== 'string' || !props.store.can('extensions.read')) return;
  const owner = new AbortController(); typeOwner = owner;
  try {
    const result = await props.store.client.read('/control/v1/extensions', extensionList, owner.signal);
    owner.signal.throwIfAborted();
    typeMetadata.value = result.items.find(item => item.extensionId === id)?.metadata;
  } catch (reason) {
    if (!owner.signal.aborted) error.value = reason instanceof Error ? reason.message : '脚本类型加载失败，请重新打开。';
  }
}, { immediate: true });
onBeforeUnmount(() => { typeOwner?.abort(); if (!presentation.value.modified) props.model.releasePristine(); });
const specializedTitle = computed(() => typeMetadata.value ?
  (rows.value.some(row => row.id === selected.value) ? t('编辑脚本实例') : t('新建脚本实例')) + ' · ' +
  metadataText(typeMetadata.value, locale.value).displayName : '');
</script>
<template>
  <nexus-modal
    :open.prop="visible"
    :size="activeKind === 'users' && !rows.some((row) => row.id === selected) ? 'normal' : 'wide'"
    :title="specializedTitle || (
      rows.some((row) => row.id === selected)
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
    <ScriptActions
        slot="header-actions"
        v-if="activeKind === 'scripts'"
        :store="store"
        :script="draft as AutomationBundle['scripts'][number]"
        :existing="rows.some((row) => row.id === selected)"
        :locked="saving || !!pending"
        @import="emit('import', $event)"
        @error="error = $event"
    />
    <div ref="form" v-draft="presentation" class="editor-pane">
      <DraftNotice :restored="presentation.state.restored" />
      <p v-if="error || scriptUnavailable" role="alert" class="error-text">
        {{ t(error || '请先选择脚本根目录并读取脚本配置。') }}
      </p>
      <p v-if="notice" role="status">{{ t(notice) }}</p>
      <p v-if="pending" role="status">
        {{ t('待确认操作：') }}{{ pending.id }}
        <button type="button" :disabled="saving || !store.can('operations.read')" @click="emit('inspect')">
          {{ t('查询操作状态并刷新') }}
        </button>
      </p>
      <form novalidate :id="'automation-editor-' + kind" @submit.prevent="emit('save', form)">
        <fieldset :disabled="saving || !!pending" :inert="saving || !!pending" class="editor-fields">
          <QueueFields
            v-if="bundle && activeKind === 'queues'"
            :model-value="draft as AutomationBundle['queues'][number]"
            v-model:schedules="queueSchedules"
            :bundle="bundle"
            :store="store"
            @update:model-value="draft = $event as AutomationDraft"
          />
          <AccountFields
            v-else-if="bundle && activeKind === 'users' && !userGlobal"
            :model-value="draft as AutomationBundle['users'][number]"
            :existing="rows.some((row) => row.id === selected)"
            :bundle="bundle"
            @update:model-value="draft = $event as AutomationDraft"
            :bindings="accountBindings"
            :store="store"
            :visible="visible"
            @update:bindings="accountBindings = $event"
            @configure="emit('configure', $event)"
          />
          <AutomationRelationEditor
            v-else-if="bundle && activeKind === 'users'"
            v-model="draft"
            :kind="activeKind"
            :bundle="bundle"
            :store="store"
          />
          <ScriptFields
            v-else-if="activeKind === 'scripts'"
            :model-value="draft as AutomationBundle['scripts'][number]"
            :store="store"
            @update:model-value="draft = $event as AutomationDraft"
          />
          <ExtensionOutlet
            v-if="(activeKind !== 'users' || userGlobal) && (activeKind !== 'scripts' || rows.some(row => row.id === selected))"
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
    <div slot="footer" class="editor-actions" :class="{ 'account-actions': activeKind === 'users' && rows.some(row => row.id === selected) }">
      <button type="button" :disabled="saving || !!pending" @click="model.discard()">{{ t('取消') }}</button>
      <button
        class="primary"
        type="submit"
        :form="'automation-editor-' + kind"
        :disabled="saving || !!pending || scriptUnavailable || !store.can('automation.write')"
      >
        {{ t(saving ? '保存中…' : '保存') }}
      </button>
    </div>
  </nexus-modal>
</template>
<style scoped src="./automation-editor-dialog.css"></style>
