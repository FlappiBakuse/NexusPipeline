import type { AutomationStore } from '../app/store-ports';
import type { AutomationDraft } from '../app/automation-draft';
import type { AutomationBundle, ControlAutomationChange } from '../control/wire.generated';
import { copyDraft } from '../app/draft-copy';
import { validateForm } from '../components/form-validation';
import { accountChanges, validAccountLimits } from './account-draft';
import { prepareHooks } from './hook-paths';
import { prepareScript } from './script-paths';
import { previewTemplate, templateKey } from './script-template';
import { AutomationEditor, entityTitles } from './automation-editor';
import type { AutomationSession } from './automation-session';
import { AutomationCommands } from './automation-commands';
import { scriptSourceKey } from './script-source';

export class AutomationSave {
  constructor(
    private readonly store: AutomationStore,
    private readonly editor: AutomationEditor,
    private readonly session: AutomationSession,
    private readonly commands: AutomationCommands
  ) {}

  async save(form?: HTMLElement): Promise<void> {
    const editor = this.editor,
      session = this.session;
    if (session.saving || session.pending || !editor.current() || !this.store.can('automation.write')) return;
    if (!validateForm(form)) return;
    if (editor.unavailable) {
      session.error = '专项适配尚未就绪，暂时无法保存此脚本实例。';
      return;
    }
    const kind = editor.activeKind.value,
      key = editor.draftKey.value,
      id = editor.selected.value;
    if (kind === 'queues' && (editor.draft.value.scriptIds as string[]).some((id) => !id)) {
      session.error = '请选择每个任务的脚本实例。';
      return;
    }
    if (
      kind === 'users' &&
      !validAccountLimits(
        this.store,
        editor.draft.value as AutomationBundle['users'][number],
        form
      )
    )
      return;
    try {
      let value = copyDraft(editor.draft.value);
      if (kind === 'scripts' && this.store.drafts.get(scriptSourceKey(id))?.selection &&
          !this.store.can('configuration.edit')) throw new Error('需要配置编辑权限才能保存所选实例。');
      const prepared =
        kind === 'scripts'
          ? prepareScript(this.store, value as AutomationBundle['scripts'][number])
          : null;
      if (prepared) value = prepared.value as AutomationDraft;
      const roots = prepared?.roots || [];
      if (kind === 'scripts' && !value.rootId) throw new Error('请选择脚本目录。');
      const revision = session.catalogs[key] ?? editor.catalog.value!.catalogRevision;
      const template = this.store.drafts.get(templateKey(id))?.bundle as AutomationBundle | undefined;
      session.saving = true;
      if (kind === 'scripts' && template) {
        const body = await previewTemplate(
          this.store,
          value as AutomationBundle['scripts'][number],
          roots,
          template,
          revision
        );
        await this.commands.commit({
          id: body.operationId,
          action: 'import',
          key,
          name: entityTitles.scripts,
          body,
          target: id,
          kind
        });
        return;
      }
      let body = { ...this.store.command(), roots, expectedCatalogRevision: revision };
      if (kind === 'users') {
        body = accountChanges(this.store, id, body);
        const user = value as AutomationBundle['users'][number];
        if (user.bindingOverrides)
          value = {
            ...value,
            bindingOverrides: {
              ...user.bindingOverrides,
              advanced: prepareHooks(
                this.store,
                user.id,
                user.bindingOverrides.advanced,
                body.roots as ControlAutomationChange['roots'][number][]
              )
            }
          };
      }
      const expectedRevision = session.revisions[key] ?? editor.revision(id);
      if (kind === 'scripts') body = { ...body, scripts: [...body.scripts, { expectedRevision, value: value as AutomationBundle['scripts'][number] }] };
      else if (kind === 'users') body = { ...body, users: [...body.users, { expectedRevision, value: value as AutomationBundle['users'][number] }] };
      else body = { ...body, queues: [...body.queues, { expectedRevision, value: value as AutomationBundle['queues'][number] }] };
      if (kind === 'queues' && editor.queueState.value) body = this.schedules(body);
      await this.commands.commit({
        id: body.operationId,
        action: 'save',
        key,
        name: entityTitles[kind],
        body,
        target: id,
        kind
      });
    } catch (error) {
      this.store.report(error);
      session.error = error instanceof Error ? error.message : '保存失败；草稿已保留。';
    } finally {
      session.saving = false;
    }
  }

  private schedules(body: ControlAutomationChange): ControlAutomationChange {
    const state = this.editor.queueState.value!;
    if (state.items.some((item) => !item.weekdays.length)) throw new Error('每个定时至少选择一天。');
    return {
      ...body,
      expectedCatalogRevision: state.catalogRevision,
      schedules: state.items.map((item) => ({
        expectedRevision: state.revisions[item.id] || 0,
        value: copyDraft(item)
      })),
      removed: state.baseline
        .filter((item) => !state.items.some((value) => value.id === item.id))
        .map((item) => ({
          kind: 'schedule' as const,
          id: item.id,
          expectedRevision: state.revisions[item.id]!
        }))
    };
  }
}
