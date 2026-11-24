import { createIdentity } from '../control/identity';
import { computed, ref, type Ref } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import { initialValue, schemaAt } from '../control/schema';
import type { AutomationDraft } from '../app/automation-draft';
import type { AutomationBundle, ControlAutomationChange, ControlAutomationImport, ControlProjectCommand } from '../control/wire.generated';
import { copyDraft } from '../app/draft-copy';
import { accountDraft, accountKey, clearAccountDraft, canCancelAccount, replaceAccountBindings, accountChanges, validAccountLimits } from './account-draft';
import { hookPathKey, prepareHooks } from './hook-paths';
import { scriptPathKey, scriptPaths, prepareScript } from './script-paths';
import { templateKey, previewTemplate } from './script-template';
import { scriptSourceKey, hasSourceSelection, saveSourceSelection } from './script-source';
import { draftPresentation } from '../app/draft-presentation';
import { ControlFailure, objectValue } from '../control/client';
import { acceptedCatalogRevision, automationImportState } from '../control/automation';
import { validateForm } from '../components/form-validation';

export type EntityKind = 'scripts' | 'users' | 'queues';
export const entityTitles: Record<EntityKind, string> = { scripts: '脚本', users: '账号', queues: '队列' };
const bindingRemovalBlocked = '请先在绑定中保存或取消配置编辑，再移除绑定。';
interface QueueDraft {
  items: AutomationBundle['schedules'];
  baseline: AutomationBundle['schedules'];
  catalogRevision: number;
  revisions: Record<string, number>;
}

export class AutomationEditor {
  readonly instance: string | undefined;
  readonly activeKind: Ref<EntityKind>;
  readonly selected = ref('');
  readonly editing = ref(false);
  // Catalog refresh can publish a new row while its creation dialog is still saving.
  readonly existing = ref(false);
  readonly userGlobal = ref(false);
  readonly catalog = computed(() => this.store.state.automation);
  readonly bundle = computed(() => this.catalog.value?.bundle);
  readonly rows = computed(() =>
    [...(this.bundle.value?.[this.activeKind.value] || [])].sort((a, b) => (a.order || 0) - (b.order || 0))
  );
  readonly draftKey = computed(
    () =>
      'automation/' +
      this.instance +
      '/' +
      this.kind() +
      '/' +
      this.activeKind.value +
      '/' +
      this.selected.value
  );
  readonly draft = computed({
    get: (): AutomationDraft => this.store.drafts.get(this.draftKey.value) as AutomationDraft || {},
    set: (value) => {
      this.store.drafts.set(this.draftKey.value, { ...value });
      this.store.dirty.add(this.draftKey.value);
    }
  });
  private readonly queueKey = computed(() => this.draftKey.value + '/schedules');
  readonly queueState = computed(
    () => this.store.drafts.get(this.queueKey.value) as QueueDraft | undefined
  );
  readonly queueSchedules = computed({
    get: () => this.queueState.value?.items || [],
    set: (items) => {
      if (this.queueState.value) this.queueState.value.items = items;
      this.store.dirty.add(this.draftKey.value);
    }
  });
  readonly accountBindings = computed({
    get: () => accountDraft(this.store, this.selected.value).items,
    set: (items) => {
      if (!replaceAccountBindings(this.store, this.selected.value, items)) {
        this.session.error = bindingRemovalBlocked;
        return;
      }
      if (this.session.error === bindingRemovalBlocked) this.session.error = '';
      this.store.dirty.add(this.draftKey.value);
    }
  });
  readonly createLabel = computed(() =>
    this.activeKind.value === 'users'
      ? '添加用户'
      : this.activeKind.value === 'scripts'
        ? '新建脚本实例'
        : '新建调度队列'
  );

  constructor(
    private readonly store: AutomationStore,
    private readonly kind: () => EntityKind,
    private readonly session: AutomationSession
  ) {
    this.instance = store.state.capabilities?.instanceId;
    this.activeKind = ref(kind());
  }

  current(): boolean {
    return this.instance === this.store.state.capabilities?.instanceId;
  }
  revision(id: string): number {
    return (
      this.catalog.value?.revisions.find(
        (item) => item.kind === this.activeKind.value.slice(0, -1) && item.id === id
      )?.revision || 0
    );
  }
  get unavailable(): boolean {
    return (
      this.editing.value &&
      this.activeKind.value === 'scripts' &&
      this.draft.value.kind === 'specialized' &&
      !scriptPaths(
        this.store,
        this.draft.value as AutomationBundle['scripts'][number]
      ).executable.trim()
    );
  }

  edit(row?: unknown, extensionId?: string): void {
    if (!this.catalog.value || this.session.saving || !this.current()) return;
    const schema = schemaAt(
      this.activeKind.value === 'scripts'
        ? 'automation-script.schema.json#/$defs/script'
        : 'automation-relations.schema.json#/$defs/' + this.activeKind.value.slice(0, -1)
    );
    const value = (row ? copyDraft(row) : initialValue(schema)) as AutomationDraft;
    if (!row) {
      const slot = this.activeKind.value === 'scripts' ? 'scripts/' + (extensionId || '') : this.activeKind.value;
      const slots = this.session.newDrafts ??= {};
      value.id = slots[slot] ??= createIdentity();
      value.order = this.rows.value.length;
      if (this.activeKind.value === 'queues') value.scheduleEnabled = false;
      if (this.activeKind.value === 'scripts') {
        value.limits = { maximumAttempts: 3, stallTimeoutSeconds: 300, totalTimeoutSeconds: 7200 };
        value.updateConfigurationAtFinish = true;
        if (extensionId) { value.kind = 'specialized'; value.extensionId = extensionId; }
      }
    }
    this.selected.value = String(value.id);
    const fresh = !this.store.drafts.has(this.draftKey.value);
    if (fresh) {
      this.store.drafts.set(this.draftKey.value, { ...value });
      this.session.revisions[this.draftKey.value] = row ? this.revision(String(value.id)) : 0;
      this.session.catalogs[this.draftKey.value] = this.catalog.value.catalogRevision;
    }
    if (this.activeKind.value === 'queues' && !this.queueState.value) this.initializeSchedules(!!row);
    if (fresh) this.formBaseline().value = this.formSnapshot();
    this.existing.value = !!row;
    this.editing.value = true;
    this.session.error = '';
    this.session.notice = '';
  }

  private formBaseline() {
    return this.store.editors.target(this.instance || '', this.activeKind.value, this.selected.value)
      .child('host-form').retain('baseline', () => ({ value: '' }));
  }
  private formSnapshot(): string {
    return JSON.stringify([this.draft.value, this.activeKind.value === 'queues' ? this.queueState.value?.items : null,
      this.activeKind.value === 'users' ? this.accountBindings.value : null]);
  }
  get edited(): boolean {
    return this.store.drafts.has(this.draftKey.value) &&
      (this.formSnapshot() !== this.formBaseline().value ||
       this.store.dirty.has(scriptPathKey(this.selected.value)));
  }
  releasePristine(): void {
    if (this.session.saving || this.session.pending || this.edited) return;
    const key = this.draftKey.value;
    this.store.drafts.delete(key); this.store.drafts.delete(this.queueKey.value);
    if (this.activeKind.value === 'users') this.store.drafts.delete(accountKey(this.store, this.selected.value));
    this.store.dirty.delete(key); delete this.session.revisions[key]; delete this.session.catalogs[key];
    this.store.editors.target(this.instance || '', this.activeKind.value, this.selected.value).child('host-form').settle('disposed');
  }
  createScript(extensionId?: string): void {
    if (!this.current() || this.session.saving || this.session.pending || !this.catalog.value) return;
    this.edit(undefined, extensionId);
  }

  resumeNew(): boolean {
    if (!this.current() || this.session.saving || this.session.pending || !this.selected.value ||
        this.rows.value.some(row => row.id === this.selected.value) || !this.store.drafts.has(this.draftKey.value))
      return false;
    this.existing.value = false;
    this.editing.value = true;
    this.session.error = '';
    return true;
  }

  private initializeSchedules(existing: boolean): void {
    const catalog = this.catalog.value!;
    const baseline = copyDraft(
      catalog.bundle.schedules.filter((item) => item.queueId === this.selected.value)
    );
    const items =
      baseline.length || existing
        ? copyDraft(baseline)
        : [
            {
              id: createIdentity(),
              queueId: this.selected.value,
              enabled: true,
              weekdays: [1, 2, 3, 4, 5],
              localMinute: 330,
              timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone,
              missedPolicy: 'skip'
            }
          ];
    this.store.drafts.set(this.queueKey.value, {
      items,
      baseline,
      catalogRevision: catalog.catalogRevision,
      revisions: Object.fromEntries(
        baseline.map((item) => [
          item.id,
          catalog.revisions.find((value) => value.kind === 'schedule' && value.id === item.id)?.revision || 0
        ])
      )
    });
  }

  discard(): void {
    if (this.session.saving || this.session.pending) return;
    const id = this.selected.value,
      key = this.draftKey.value;
    if (!this.store.editors.target(this.instance || '', this.activeKind.value, id).canCancel() ||
        this.activeKind.value === 'users' && !canCancelAccount(this.store, id)) {
      this.session.error = '原操作尚未完成，请保留草稿并稍后查询。';
      return;
    }
    this.store.drafts.delete(key);
    this.store.dirty.delete(key);
    this.store.dirty.delete(scriptPathKey(id));
    delete this.session.revisions[key];
    delete this.session.catalogs[key];
    if (this.activeKind.value === 'queues') this.store.drafts.delete(this.queueKey.value);
    if (this.activeKind.value === 'users') clearAccountDraft(this.store, id);
    this.store.editors.settle(this.instance || '', this.activeKind.value, id, 'cancel');
    for (const [slot, value] of Object.entries(this.session.newDrafts || {}))
      if (value === id) delete this.session.newDrafts![slot];
    this.store.drafts.delete(hookPathKey(this.store, id));
    if (this.activeKind.value === 'scripts') this.store.drafts.delete(scriptPathKey(id));
    this.store.drafts.delete(templateKey(id));
    this.store.drafts.delete(scriptSourceKey(id));
    this.editing.value = false;
    this.selected.value = '';
    this.session.error = '';
    this.session.notice = '';
  }
}

export type AutomationAction = {
  id: string;
  action: 'save' | 'remove' | 'move' | 'import' | 'avatar';
  key?: string;
  name?: string;
  target: string;
  kind: 'scripts' | 'users' | 'queues';
  body: ControlAutomationChange | ControlAutomationImport;
  sourceSelection?: ControlProjectCommand;
  candidates?: readonly string[];
  definitionCommitted?: boolean;
  sourcePending?: boolean;
};
export type AutomationSession = {
  newDrafts?: Record<string, string>;
  revisions: Record<string, number>;
  catalogs: Record<string, number>;
  pending: AutomationAction | null;
  saving: boolean;
  error: string;
  notice: string;
};

export function automationSession(store: AutomationStore, page: string): AutomationSession {
  const key = 'automation/page/' + store.state.capabilities?.instanceId + '/' + page;
  if (!store.drafts.has(key))
    store.drafts.set(key, {
      revisions: {},
      catalogs: {},
      pending: null,
      saving: false,
      error: '',
      notice: ''
    });
  return store.drafts.get(key) as unknown as AutomationSession;
}

const sourcePending = '定义已提交，所选配置等待处理。';
const sourceUnknown = '定义已提交，所选配置结果待确认。';
const sourceStatus = (action: AutomationAction) => action.sourceSelection ? sourceUnknown : sourcePending;

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
    const authority = this.store.client.authoritySignal;
    const pending = { ...action, body, sourcePending: hasSourceSelection(this.store, action) };
    this.session.pending = pending;
    this.session.error = '';
    this.session.notice = '';
    this.store.dirty.add('automation-pending/' + action.id);
    try {
      if (body.format === 'nexus.control.automation-import') {
        if (!(await this.confirmImport(pending, authority))) return null;
      } else {
        const candidates = pending.candidates?.length ? [...pending.candidates] : undefined;
        await this.store.client.write(
          candidates ? '/control/v1/automation/change-drafts' : '/control/v1/automation/change',
          candidates ? { change: body, candidates } : body,
          value => acceptedCatalogRevision(value, body), body.operationId, authority
        );
      }
      if (!this.current() || authority.aborted) return null;
      pending.definitionCommitted = true;
      return await this.finish(pending, authority);
    } catch (reason) {
      if (!this.current() || authority.aborted) return null;
      if (reason instanceof ControlFailure && !reason.uncertain && !pending.definitionCommitted) {
        this.session.pending = null;
        this.store.dirty.delete('automation-pending/' + action.id);
      }
      if (this.resumeSource(pending, reason))
        this.session.error = '实例已保存，所选配置未保存；请修正草稿后重试。';
      else if (pending.definitionCommitted && pending.sourcePending)
        this.session.error = sourceStatus(pending);
      else if (!this.session.pending)
        this.session.error = reason instanceof Error ? reason.message : '保存失败，草稿仍在。';
      return null;
    } finally {
      this.session.saving = false;
    }
  }

  async check(): Promise<AutomationAction | null> {
    const pending = this.session.pending;
    if (!pending || this.session.saving || !this.current()) return null;
    const authority = this.store.client.authoritySignal;
    const refreshOnly = pending.definitionCommitted && !pending.sourcePending;
    if (!refreshOnly && !this.store.can('operations.read')) return null;
    this.session.saving = true;
    this.session.error = '';
    this.session.notice = '';
    try {
      if (!refreshOnly) {
        if (!(await this.completed(pending, authority))) return null;
        if (pending.body.format === 'nexus.control.automation-import' && !pending.definitionCommitted &&
          !(await this.confirmImport(pending, authority))) return null;
        pending.definitionCommitted = true;
      }
      return await this.finish(pending, authority);
    } catch (reason) {
      if (!this.current() || authority.aborted) return null;
      this.session.error = this.resumeSource(pending, reason)
        ? '实例已保存，所选配置未保存；请修正草稿后重试。'
        : pending.definitionCommitted && pending.sourcePending
          ? sourceStatus(pending) : '尚无法确认操作结果；草稿已保留，原命令仍被锁定。';
      return null;
    } finally {
      this.session.saving = false;
    }
  }

  private async confirmImport(action: AutomationAction, authority: AbortSignal): Promise<boolean> {
    const body = action.body;
    if (body.format !== 'nexus.control.automation-import') throw new TypeError('automation_import_result_invalid');
    // A status check replays only the frozen request after the original operation is known to exist.
    const state = await this.store.client.write('/control/v1/automation/import/apply', body,
      value => automationImportState(value, body), body.operationId, authority);
    if (!this.current() || authority.aborted || this.session.pending?.id !== action.id) return false;
    if (state === 'published') return true;
    if (state === 'cancelled') {
      this.session.pending = null;
      this.store.dirty.delete('automation-pending/' + action.id);
      this.session.error = '导入未执行，草稿已保留。';
    } else if (state === 'quarantined') this.session.error = '导入已隔离，草稿已保留。';
    else this.session.notice = '操作仍在处理，请稍后再查询。';
    return false;
  }

  private async completed(action: AutomationAction, authority: AbortSignal): Promise<boolean> {
    const state = await this.store.client.read('/control/v1/operations/' + action.id, objectValue, authority);
    if (!this.current() || authority.aborted || this.session.pending?.id !== action.id) return false;
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

  private async finish(action: AutomationAction, authority: AbortSignal): Promise<AutomationAction | null> {
    const body = action.body;
    const expected = (body.format === 'nexus.control.automation-change' ? body.expectedCatalogRevision : body.expectedRevision) + 1;
    const current = () => this.current() && !authority.aborted && this.session.pending?.id === action.id;
    try { await this.store.refreshCatalog(expected, authority); }
    catch {
      if (current())
        this.session.error = action.sourcePending ? sourceStatus(action) : '已提交，等待刷新。';
      return null;
    }
    if (!current()) return null;
    if (action.sourcePending)
      await saveSourceSelection(this.store, action, current);
    if (!current()) return null;
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
    this.store.dirty.delete('automation-pending/' + action.id);
    this.session.pending = null;
    this.session.error = '';
    return action;
  }
}

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
    if (kind === 'users' && !canCancelAccount(this.store, id)) {
      session.error = '请先完成配置草稿中的编辑，再保存账号。';
      return;
    }
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
        candidates: kind === 'users' ? Object.values(accountDraft(this.store, id).candidates) : undefined,
        target: id,
        kind
      });
    } catch (error) {
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
