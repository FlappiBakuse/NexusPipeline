<script setup lang="ts">
import { computed, ref, shallowRef, watch, toRefs } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import ModalTransition from '../components/ModalTransition.vue';
import BindingEditorDialog from './BindingEditorDialog.vue';
import AutomationRows from './AutomationRows.vue';
import AutomationEditorDialog from './AutomationEditorDialog.vue';
import AutomationDeleteDialog from './AutomationDeleteDialog.vue';
import ScriptTypes, { type ScriptChoice } from './ScriptTypes.vue';
import type { ExtensionState } from '../control/extensions';
import { scriptPathKey } from './script-paths';
import type { AutomationDraft } from '../app/automation-draft';
import type { AutomationBundle } from '../control/wire.generated';
import AutomationExchange from './AutomationExchange.vue';
import { automationSession, type AutomationAction } from './automation-session';
import { t } from '../app/locale';
import { copyDraft } from '../app/draft-copy';
import './automation-messages';
import { AutomationCommands } from './automation-commands';
import { loadTemplate, templateKey } from './script-template';
import { AutomationEditor, type EntityKind } from './automation-editor';
import { AutomationSave } from './automation-save';
import { useAccountSummaries } from './account-summary';
const props = defineProps<{ store: AutomationStore; kind: EntityKind; visible: boolean }>();
const summaries = useAccountSummaries(props.store, () => props.visible && props.kind === 'users');
const session = automationSession(props.store, props.kind);
const { saving, error, notice, pending } = toRefs(session);
const editorModel = new AutomationEditor(props.store, () => props.kind, session);
const { activeKind, selected, editing, userGlobal, draftKey, draft, catalog, bundle, rows, createLabel } =
  editorModel;
const instanceId = editorModel.instance;
const isCurrent = () => editorModel.current();
const commands = new AutomationCommands(props.store, session, (action) => {
  if (action.key === draftKey.value && (action.action === 'save' || action.action === 'import'))
    editing.value = false;
});
const saveOwner = new AutomationSave(props.store, editorModel, session, commands);
const nativeBinding = ref('');
const bindingEditor = computed(() => {
  const binding = bundle.value?.bindings.find(row => row.id === nativeBinding.value);
  const script = bundle.value?.scripts.find(row => row.id === binding?.scriptId);
  const revision = (kind: string, id: string) => catalog.value?.revisions.find(row => row.kind === kind && row.id === id)?.revision ?? 0;
  return binding && script ? {
    bindingId: binding.id, bindingRevision: revision('binding', binding.id),
    scriptRevision: revision('script', script.id), extensionId: script.extensionId
  } : null;
});
const deleting = ref<{ id: string; name: string; revision: number } | null>(null);
const exchangeOpen = ref(false);
const exchangeMounted = ref(false);
const importFile = ref<File>();
const typesOpen = ref(false);
const scriptCatalog = shallowRef<readonly ExtensionState[]>();
const listedRows = computed(() => {
  const values = bundle.value?.[props.kind] || [];
  return [...values].sort((a, b) => Number('order' in a ? a.order : 0) - Number('order' in b ? b.order : 0));
});
const emptyText = computed(
  () =>
    ({
      users: ['暂无用户', '点击右上角「添加用户」创建用户后，再为它绑定一个或多个脚本实例。'],
      scripts: ['暂无脚本实例', '创建一个脚本实例后，它会出现在这里并可加入调度队列。'],
      queues: ['暂无调度队列', '把多个脚本串成一个可重复执行的工作流。']
    })[props.kind]
);
function beginCreate(): void {
  activeKind.value = props.kind;
  userGlobal.value = false;
  if (activeKind.value === 'scripts') typesOpen.value = true;
  else edit();
}
function chooseScript(choice?: ScriptChoice): void {
  typesOpen.value = false;
  editorModel.createScript(choice?.extensionId);
}
async function importScript(file: File): Promise<void> {
  if (saving.value || pending.value) return;
  const id = selected.value,
    key = draftKey.value;
  saving.value = true;
  error.value = '';
  try {
    const imported = await loadTemplate(props.store, file, id);
    if (!isCurrent() || selected.value !== id || draftKey.value !== key) return;
    if (imported.bundle.attachments.length) {
      importFile.value = file;
      exchangeMounted.value = true;
      exchangeOpen.value = true;
      return;
    }
    draft.value = imported.value as AutomationDraft;
    props.store.drafts.set(scriptPathKey(id), imported.paths);
    props.store.drafts.set(templateKey(id), { bundle: imported.bundle });
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : '导入文件无法解析。';
  } finally {
    saving.value = false;
  }
}
const edit = (row?: unknown) => editorModel.edit(row);
function askRemove(row: { id: string; name: string }): void {
  if (!pending.value && !saving.value)
    deleting.value = { id: row.id, name: row.name, revision: editorModel.revision(row.id) };
}
async function remove(name?: string): Promise<void> {
  const target = deleting.value;
  if (!target || !props.visible || saving.value || pending.value || !props.store.can('automation.delete') ||
      (props.kind === 'users' && name !== target.name)) return;
  activeKind.value = props.kind;
  selected.value = target.id;
  deleting.value = null;
  saving.value = true;
  error.value = '';
  const body = props.store.command();
  (body.removed as unknown[]).push({
    kind: activeKind.value.slice(0, -1),
    id: target.id,
    expectedRevision: target.revision
  });
  await commit(body, {
    id: body.operationId,
    action: 'remove',
    key: 'automation/' + instanceId + '/' + props.kind + '/' + activeKind.value + '/' + target.id,
    name: target.name
  });
}
async function commit(
  body: AutomationAction['body'],
  action: Pick<AutomationAction, 'id' | 'action' | 'key' | 'name'>
): Promise<void> {
  const completed = await commands.commit({
    ...action,
    body,
    target: selected.value,
    kind: activeKind.value
  });
  if (completed?.key === draftKey.value && (completed.action === 'save' || completed.action === 'import'))
    editing.value = false;
}
async function checkPending(): Promise<void> {
  const completed = await commands.check();
  if (completed?.key === draftKey.value && (completed.action === 'save' || completed.action === 'import'))
    editing.value = false;
}
async function reorder(ids: string[]): Promise<void> {
  if (pending.value || saving.value) return;
  const body = props.store.command();
  for (const [order, id] of ids.entries()) {
    const row = rows.value.find((item) => item.id === id);
    if (!row) return;
    (body[activeKind.value] as unknown[]).push({
      expectedRevision: editorModel.revision(id),
      value: { ...copyDraft(row), order }
    });
  }
  saving.value = true;
  await commit(body, { id: body.operationId, action: 'move' });
}
async function saveAvatar(
  user: AutomationBundle['users'][number],
  revision: number,
  catalogRevision: number
): Promise<void> {
  if (saving.value || pending.value || !isCurrent() || !props.store.can('automation.write')) return;
  const body = {
    ...props.store.command(),
    expectedCatalogRevision: catalogRevision,
    users: [{ expectedRevision: revision, value: user }]
  };
  selected.value = user.id;
  saving.value = true;
  await commit(body, { id: body.operationId, action: 'avatar' });
}
watch(
  () => props.kind,
  (value) => {
    activeKind.value = value;
  }
);
watch(editing, (value) => {
  if (!value) activeKind.value = props.kind;
});
watch(() => props.visible, visible => { if (!visible) deleting.value = null; });
function openRow(row: unknown, global = false): void {
  activeKind.value = props.kind;
  userGlobal.value = global;
  edit(row);
}
</script>
<template>
  <PageActions :visible="visible">
    <button
      class="primary"
      :disabled="!catalog || saving || !!pending || !store.can('automation.write')"
      @click="beginCreate"
    >
      {{ t(createLabel) }}
    </button>
    <button v-if="pending && !editing" :disabled="saving" @click="checkPending">{{ t('查询操作状态并刷新') }}</button>
  </PageActions>
  <ScriptTypes v-if="kind === 'scripts'" v-model:installed="scriptCatalog" :store="store" :active="visible"
    :visible="visible && typesOpen" @choose="chooseScript" @close="typesOpen = false" />
  <div class="entity-list-page">
    <template v-if="visible && !editing">
      <nexus-toast v-if="error" :tone="pending ? 'warning' : 'danger'" :message.prop="t(error)" />
      <nexus-toast v-else-if="notice" :message.prop="t(notice)" @nexus-close="notice = ''" />
      <nexus-toast v-else-if="pending && !saving" tone="warning" :message.prop="t('操作结果待确认；请查询状态，勿重复提交。')" />
      <nexus-toast v-else-if="!pending && kind === 'users' && summaries.error" tone="danger" :message.prop="t(summaries.error)" />
    </template>
    <section class="entity-list">
      <nexus-empty-state
        v-if="!listedRows.length"
        class="automation-empty"
        :title="t(emptyText[0]!)"
        :description="t(emptyText[1]!)"
      />
      <AutomationRows
        v-else-if="bundle"
        :kind="kind"
        :rows="listedRows"
        :bundle="bundle"
        :script-catalog="scriptCatalog"
        :store="store"
        :visible="visible"
        :locked="saving || !!pending"
        :summaries="summaries.items"
        :summary-now="summaries.now"
        @edit="openRow($event)"
        @global="openRow($event, true)"
        @remove="askRemove"
        @reorder="reorder"
        @avatar="saveAvatar"
        @error="error = $event"
      />
    </section>
    <ModalTransition>
      <AutomationEditorDialog
        v-if="editing"
        :store="store"
        :kind="kind"
        :visible="visible && !nativeBinding"
        :model="editorModel"
        :session="session"
        :script-type="scriptCatalog?.find(item => item.extensionId === draft.extensionId)"
        @close="editing = false"
        @import="importScript"
        @configure="nativeBinding = $event"
        @inspect="checkPending"
        @save="saveOwner.save($event)"
      />
    </ModalTransition>
    <nexus-modal
      v-if="kind === 'scripts' && exchangeMounted"
      :open.prop="visible && exchangeOpen"
      size="wide"
      :title="t('导入脚本实例')"
      @nexus-close="exchangeOpen = false"
    >
      <AutomationExchange
        :store="store"
        :file="importFile"
        @applied="
          exchangeOpen = false;
          editing = false;
          notice = '导入已受理，目录已刷新。';
        "
      />
    </nexus-modal>
  </div>
  <ModalTransition>
    <BindingEditorDialog v-if="bindingEditor" :key="bindingEditor.bindingId" v-bind="bindingEditor" :store="store" :visible="visible"
      @close="nativeBinding = ''" @changed="notice = t('已保存')" />
  </ModalTransition>
  <AutomationDeleteDialog :target="deleting" :kind="kind" :visible="visible"
    @confirm="remove" @cancel="deleting = null" />
</template>
<style scoped src="./automation-page.css"></style>
