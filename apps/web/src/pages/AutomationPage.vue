<script setup lang="ts">
import { computed, ref, watch, toRefs } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import ModalTransition from '../components/ModalTransition.vue';
import ConfigurationPanel from './ConfigurationPanel.vue';
import AutomationRows from './AutomationRows.vue';
import AutomationEditorDialog from './AutomationEditorDialog.vue';
import ScriptTypes, { type ScriptChoice } from './ScriptTypes.vue';
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
const configBinding = ref('');
const deleting = ref<{ id: string; name: string } | null>(null);
const exchangeOpen = ref(false);
const exchangeMounted = ref(false);
const importFile = ref<File>();
const typesOpen = ref(false);
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
function askRemove(row: { id: string }): void {
  if (!pending.value && !saving.value)
    deleting.value = { id: row.id, name: label(row as AutomationDraft) };
}
async function remove(): Promise<void> {
  if (!deleting.value || saving.value || pending.value || !props.store.can('automation.delete')) return;
  const target = deleting.value;
  selected.value = target.id;
  deleting.value = null;
  saving.value = true;
  error.value = '';
  const body = props.store.command();
  (body.removed as unknown[]).push({
    kind: activeKind.value.slice(0, -1),
    id: target.id,
    expectedRevision: editorModel.revision(target.id)
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
function label(row: AutomationDraft): string {
  return String(row.name || row.id);
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
  <ModalTransition>
    <ScriptTypes
      v-if="kind === 'scripts'"
      :store="store"
      :active="visible"
      :visible="visible && typesOpen"
      @choose="chooseScript"
      @close="typesOpen = false"
    />
  </ModalTransition>
  <div class="entity-list-page">
    <nexus-toast v-if="kind === 'users' && summaries.error" tone="danger" :message.prop="t(summaries.error)" />
    <nexus-toast v-if="error && !editing" tone="danger" :message.prop="t(error)" />
    <nexus-toast v-if="pending && !editing" tone="warning" :message.prop="t('操作结果待确认；请查询状态，勿重复提交。')" />
    <nexus-toast v-if="notice" :message.prop="t(notice)" @nexus-close="notice = ''" />
    <section class="entity-list">
      <div v-if="!listedRows.length" class="automation-empty">
        <strong>{{ t(emptyText[0]!) }}</strong>
        <p>{{ t(emptyText[1]!) }}</p>
      </div>
      <AutomationRows
        v-else-if="bundle"
        :kind="kind"
        :rows="listedRows"
        :bundle="bundle"
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
        :visible="visible && !configBinding"
        :model="editorModel"
        :session="session"
        @close="editing = false"
        @import="importScript"
        @configure="configBinding = $event"
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
    <ConfigurationPanel
      v-if="configBinding"
      :store="store"
      :binding-id="configBinding"
      :visible="visible"
      @close="configBinding = ''"
    />
  </ModalTransition>
  <ModalTransition>
    <nexus-confirm-dialog
      v-if="deleting"
      :open.prop="visible"
      :title="t('删除自动化项目')"
      :message="`${t('确定删除')} ${deleting.name}? ${t('关联定义将由 Host 检查。')}`"
      tone="danger"
      @nexus-confirm="remove"
      @nexus-cancel="deleting = null"
      @nexus-close="deleting = null"
    />
  </ModalTransition>
</template>

<style scoped src="./automation-page.css"></style>
