import type { AutomationStore } from '../app/store-ports';
import { draftPresentation } from '../app/draft-presentation';
import { ControlFailure, objectValue } from '../control/client';
import { copyDraft } from '../app/draft-copy';
import type { AutomationAction, AutomationSession } from './automation-session';
import { clearAccountDraft } from './account-draft';
import { hookPathKey } from './hook-paths';
import { scriptPathKey } from './script-paths';
import { saveSourceSelection, scriptSourceKey } from './script-source';

export class AutomationCommands {
  private readonly instanceId: string | undefined;

  constructor(
    private readonly store: AutomationStore,
    private readonly session: AutomationSession,
    private readonly completedView: (action: AutomationAction) => void
  ) {
    this.instanceId = store.state.capabilities?.instanceId;
  }

  private current(): boolean {
    return this.store.state.capabilities?.instanceId === this.instanceId;
  }

  async commit(action: AutomationAction): Promise<AutomationAction | null> {
    if (!this.current() || this.session.pending) return null;
    const body = copyDraft(action.body);
    const pending = { ...action, body };
    this.session.pending = pending;
    this.store.dirty.add('automation-pending/' + action.id);
    try {
      const isImport = body.format === 'nexus.control.automation-import';
      await this.store.client.write(
        isImport ? '/control/v1/automation/import/apply' : '/control/v1/automation/change',
        body,
        (value) => {
          const result = objectValue(value);
          if (result.operationId !== body.operationId) throw new TypeError('automation_result_invalid');
          if (
            body.format === 'nexus.control.automation-change' &&
            (result.catalogRevision !== body.expectedCatalogRevision + 1 || typeof result.isNew !== 'boolean')
          )
            throw new TypeError('automation_result_invalid');
          return result;
        },
        body.operationId
      );
      if (!this.current()) return null;
      if (isImport && !(await this.completed(pending))) return null;
      pending.definitionCommitted = true;
      return await this.finish(pending);
    } catch (reason) {
      this.store.report(reason);
      if (reason instanceof ControlFailure && !reason.uncertain && !pending.definitionCommitted) {
        this.session.pending = null;
        this.store.dirty.delete('automation-pending/' + action.id);
      }
      this.resumeSource(pending, reason);
      this.session.error = this.session.pending
        ? '操作结果待确认；请查询状态，勿重复提交。'
        : reason instanceof Error
          ? reason.message
          : '保存失败，草稿仍在。';
      return null;
    } finally {
      this.session.saving = false;
    }
  }

  async check(): Promise<AutomationAction | null> {
    if (!this.session.pending || this.session.saving || !this.current() || !this.store.can('operations.read'))
      return null;
    const pending = this.session.pending;
    this.session.saving = true;
    this.session.error = '';
    try {
      if (!(await this.completed(pending))) return null;
      pending.definitionCommitted = true;
      return await this.finish(pending);
    } catch (reason) {
      this.store.report(reason);
      this.session.error = this.resumeSource(pending, reason)
        ? '实例已保存，所选配置未保存；请修正草稿后重试。'
        : '尚无法确认操作结果；草稿已保留，原命令仍被锁定。';
      return null;
    } finally {
      this.session.saving = false;
    }
  }

  private async completed(action: AutomationAction): Promise<boolean> {
    const state = await this.store.client.read('/control/v1/operations/' + action.id, objectValue);
    const kind = action.action === 'import' ? 'automation.import' : 'automation.change';
    if (state.operationId !== action.id || state.kind !== kind)
      throw new Error('操作类型不匹配，需人工核对。');
    if (state.status === 'completed') return true;
    this.session.notice = '操作仍在处理，请稍后再查询。';
    return false;
  }

  private resumeSource(action: AutomationAction, reason: unknown): boolean {
    if (!(reason instanceof ControlFailure) || reason.uncertain || !action.definitionCommitted ||
        !action.sourceSelection || !action.key || !this.current()) return false;
    const catalog = this.store.state.automation;
    const revision = catalog?.revisions.find(item => item.kind === 'script' && item.id === action.target)?.revision;
    if (!catalog || revision === undefined) return false;
    this.session.catalogs[action.key] = catalog.catalogRevision;
    this.session.revisions[action.key] = revision;
    this.session.pending = null;
    this.store.dirty.delete('automation-pending/' + action.id);
    return true;
  }

  private async finish(action: AutomationAction): Promise<AutomationAction | null> {
    await this.store.refresh();
    if (!this.current() || this.session.pending?.id !== action.id) return null;
    await saveSourceSelection(this.store, action, () => this.current() && this.session.pending?.id === action.id);
    if (!this.current() || this.session.pending?.id !== action.id) return null;
    this.completedView(action);
    if ((action.action === 'save' || action.action === 'import') && action.key) {
      draftPresentation(this.store.editors.target(this.instanceId || '', action.kind, action.target).child('host-form'), true).saved();
      this.store.dirty.delete(action.key);
      this.store.drafts.delete(action.key);
      delete this.session.revisions[action.key];
      delete this.session.catalogs[action.key];
      if (action.kind === 'queues') this.store.drafts.delete(action.key + '/schedules');
      if (action.kind === 'users') clearAccountDraft(this.store, action.target, 'saved');
      this.store.editors.saved(this.instanceId || '', action.kind, action.target);
      for (const [slot, value] of Object.entries(this.session.newDrafts || {}))
        if (value === action.target) delete this.session.newDrafts![slot];
      this.store.drafts.delete(hookPathKey(this.store, action.target));
      if (action.kind === 'scripts') {
        this.store.drafts.delete(scriptPathKey(action.target));
        this.store.dirty.delete(scriptPathKey(action.target));
        this.store.drafts.delete('script-template/' + action.target);
        this.store.drafts.delete(scriptSourceKey(action.target));
      }
      this.session.notice = '项目已保存。';
    } else if (action.action === 'remove') {
      if (action.key) {
        this.store.dirty.delete(action.key);
        this.store.drafts.delete(action.key);
        if (action.kind === 'queues') this.store.drafts.delete(action.key + '/schedules');
        delete this.session.revisions[action.key];
        delete this.session.catalogs[action.key];
      }
      this.session.notice = '项目已删除。';
    } else this.session.notice = action.action === 'avatar' ? '头像已更新' : '顺序已保存。';
    if (this.store.state.uncertainOperation === action.id) {
      this.store.state.uncertainOperation = '';
      this.store.state.error = '';
    }
    this.store.dirty.delete('automation-pending/' + action.id);
    this.session.pending = null;
    this.session.error = '';
    return action;
  }
}
