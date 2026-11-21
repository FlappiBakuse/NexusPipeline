<script setup lang="ts">
import { computed, ref, toRefs, onBeforeUnmount, type ComputedRef } from 'vue';
import type { ExtensionState } from '../control/extensions';
import { metadataText } from '../control/extension-metadata';
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
const scriptUnavailable = computed(() => props.model.unavailable);
onBeforeUnmount(() => { if (!presentation.value.modified) props.model.releasePristine(); });
const specializedTitle = computed(() => props.scriptType && activeKind.value === 'scripts' ?
  (existing.value ? t('编辑脚本实例') : t('新建脚本实例')) + ' · ' +
  (props.scriptType.metadata ? metadataText(props.scriptType.metadata, locale.value).displayName :
    props.scriptType.artifactName || props.scriptType.extensionId) : '');
</script>
<template>
  <nexus-modal
    :open.prop="visible"
    :size="activeKind === 'users' && !existing ? 'normal' : 'wide'"
    :title="specializedTitle || (
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
      <form novalidate :id="'automation-editor-' + kind" @submit.prevent="emit('save', form)">
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
          <AccountFields
            v-else-if="bundle && activeKind === 'users' && !userGlobal"
            :model-value="draft as AutomationBundle['users'][number]"
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
      <button v-if="pending" type="button" :disabled="saving || !store.can('operations.read')" @click="emit('inspect')">
        {{ t('查询操作状态并刷新') }}
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
    </div>
  </nexus-modal>
</template>
<style scoped src="./automation-editor-dialog.css"></style>
