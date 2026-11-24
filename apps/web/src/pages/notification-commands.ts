import { createIdentity } from '../control/identity';
import { ControlFailure, integerValue, objectValue, type ControlClient } from '../control/client';
import type { ControlNotificationCommandInput, ControlNotificationCommandSecret, ControlNotificationCommand } from '../control/wire.generated';
import { copyDraft } from '../app/draft-copy';
import { reactive } from 'vue';
import { secretFields, type SecretField, type SecretState, notificationCatalog, secretStates } from './notification-types';
import type { EditorDraftStore } from '../app/editor-drafts';

export interface NotificationSession extends Record<string, unknown> {
  draft: ControlNotificationCommandInput;
  baseline: string;
  catalogRevision: number;
  taskRevision: number;
  taskId: string;
  pending: string;
  pendingAction: 'save' | 'test' | '';
  busy: boolean;
  error: string;
  notice: string;
  completed: boolean;
  conflict: boolean;
}
export class NotificationCommands {
  constructor(
    private readonly port: NotificationEditorPort,
    private readonly session: NotificationSession,
    private readonly credentials: NotificationCredentials,
    private readonly existing: boolean,
    private readonly validate: () => boolean
  ) {}
  private target(operationId: string) {
    return {
      operationId,
      taskId: this.session.taskId,
      expectedCatalogRevision: this.session.catalogRevision,
      expectedTaskRevision: this.session.taskRevision
    };
  }
  private dirty(): boolean {
    return JSON.stringify(this.session.draft) !== this.session.baseline || this.credentials.dirty;
  }
  private clearPending(): void {
    this.session.pending = '';
    this.session.pendingAction = '';
    this.port.markDirty(this.dirty());
  }
  private finishSaved(): void {
    if (!this.port.current()) return;
    this.credentials.clear();
    this.session.baseline = JSON.stringify(this.session.draft);
    this.clearPending();
    this.port.removeDraft();
    this.session.completed = true;
  }
  async save(): Promise<void> {
    if (
      !this.port.current() ||
      this.session.busy ||
      this.session.pending ||
      this.session.conflict ||
      !this.credentials.view.ready ||
      !this.port.can('notifications.write')
    )
      return;
    if (!this.validate()) return;
    this.session.busy = true;
    this.session.error = '';
    this.session.notice = '';
    const id = createIdentity();
    try {
      const input = copyDraft(this.session.draft);
      const secrets = this.credentials.edits();
      this.session.pending = id;
      this.session.pendingAction = 'save';
      await this.port.save(this.target(id), input, secrets);
      this.finishSaved();
    } catch (reason) {
      this.session.error = reason instanceof Error ? reason.message : '保存失败；草稿已保留';
      if (reason instanceof ControlFailure && reason.status === 409) this.session.conflict = true;
      if (!(reason instanceof ControlFailure && reason.uncertain)) this.clearPending();
    } finally {
      this.session.busy = false;
    }
  }
  async test(): Promise<void> {
    if (
      !this.port.current() ||
      !this.existing ||
      this.session.busy ||
      this.session.pending ||
      this.dirty() ||
      !this.port.can('notifications.test')
    ) {
      this.session.notice = '先保存当前修改，再测试已保存的任务。';
      return;
    }
    this.session.busy = true;
    this.session.error = '';
    const id = createIdentity();
    try {
      this.session.pending = id;
      this.session.pendingAction = 'test';
      await this.port.testSaved(this.target(id));
      this.session.notice = '已将已保存任务送入测试队列。';
      this.clearPending();
    } catch (reason) {
      this.session.error = reason instanceof Error ? reason.message : '测试请求失败';
      if (!(reason instanceof ControlFailure && reason.uncertain)) this.clearPending();
    } finally {
      this.session.busy = false;
    }
  }
  async checkPending(): Promise<void> {
    const { pending, pendingAction } = this.session;
    if (!pending || !pendingAction || this.session.busy || !this.port.can('operations.read'))
      return;
    this.session.busy = true;
    try {
      if (await this.port.checkOperation(pending, pendingAction)) {
        if (pendingAction === 'save') this.finishSaved();
        else {
          this.clearPending();
          this.session.notice = '已确认测试请求送入队列。';
          this.session.error = '';
        }
      } else this.session.notice = '通知操作仍在处理，请稍后查询。';
    } catch {
      this.session.error = '无法确认原通知操作；草稿与操作 ID 已保留。';
    } finally {
      this.session.busy = false;
    }
  }
}

export class NotificationCredentials {
  readonly view;
  private owner?: AbortController;
  private alive = true;

  constructor(
    private readonly port: Pick<NotificationEditorPort, 'current' | 'readCredentials' | 'drafts'>,
    private readonly taskId: string | undefined,
    private readonly draft: () => ControlNotificationCommandInput,
    private readonly revision: () => number,
    private readonly failed: (message: string) => void
  ) {
    this.view = port.drafts.protect('credentials', () => reactive({
      statuses: [] as SecretState[],
      ready: false,
      modes: Object.fromEntries(secretFields.map((field) => [field, 'keep'])) as Record<
        SecretField,
        'keep' | 'set' | 'clear'
      >,
      values: Object.fromEntries(secretFields.map((field) => [field, ''])) as Record<SecretField, string>
    }), view => {
      for (const field of secretFields) { view.values[field] = ''; view.modes[field] = 'keep'; }
    });
    this.view.ready = !taskId || this.view.ready;
  }

  get shown(): SecretField[] {
    const draft = this.draft();
    if (draft.kind === 'smtp') return ['smtp-password'];
    return [
      'webhook-url',
      'webhook-secret',
      ...(draft.configuration.webhookKind === 'feishu' ? (['feishu-app-secret'] as SecretField[]) : []),
      ...(draft.configuration.webhookKind === 'slack' ? (['slack-bot-token'] as SecretField[]) : [])
    ];
  }

  get dirty(): boolean {
    return secretFields.some((field) => this.view.modes[field] !== 'keep');
  }

  async read(id: string): Promise<void> {
    if (id !== this.taskId || !this.alive || !this.port.current()) return;
    this.owner?.abort();
    this.view.ready = false;
    const owner = new AbortController();
    this.owner = owner;
    try {
      const statuses = await this.port.readCredentials(id, this.revision(), owner.signal);
      owner.signal.throwIfAborted();
      if (this.alive && this.port.current()) {
        this.view.statuses = statuses;
        this.view.ready = true;
      }
    } catch (error) {
      if (!owner.signal.aborted && this.alive && this.port.current()) {
        this.failed(error instanceof Error ? error.message : '凭据状态读取失败');
      }
    }
  }

  edit(field: SecretField, value: string): void {
    this.view.values[field] = value;
    this.view.modes[field] = value ? 'set' : 'keep';
  }


  edits(): ControlNotificationCommandSecret[] {
    return this.shown.flatMap((field) => {
      const disposition = this.view.modes[field];
      if (disposition === 'keep' && !this.taskId) return [];
      if (disposition === 'set' && !this.view.values[field]) throw new Error('缺少新值');
      return [
        {
          field,
          disposition,
          expectedRevision: this.view.statuses.find((item) => item.field === field)?.revision ?? 0,
          ...(disposition === 'set' ? { value: this.view.values[field] } : {})
        }
      ];
    });
  }

  clear(): void {
    for (const field of secretFields) {
      this.view.values[field] = '';
      this.view.modes[field] = 'keep';
    }
  }

  dispose(): void {
    this.alive = false;
    this.owner?.abort();
  }
}

interface NotificationTarget {
  operationId: string;
  taskId: string;
  expectedCatalogRevision: number;
  expectedTaskRevision: number;
}

type EditorAction = 'save' | 'test';
type EditorCapability = 'notifications.write' | 'notifications.test' | 'operations.read';
const route = '/control/v1/notifications';

function command(action: EditorAction, target: NotificationTarget): ControlNotificationCommand {
  return { format: 'nexus.control.notification-command', version: 1, action, ...target };
}

export function notificationEditorPort(
  store: {
    state: { capabilities: { instanceId: string } | null };
    client: ControlClient;
    can(capability: EditorCapability): boolean;
    drafts: Map<string, Record<string, unknown>>;
    dirty: Set<string>;
    editors: EditorDraftStore;
  },
  instanceId: string,
  draftName: string
) {
  const { client, state, drafts, dirty } = store;
  const key = 'notification/editor/' + instanceId + '/' + draftName;
  const memory = () => store.editors.target(instanceId, 'notification', draftName);
  const current = () => state.capabilities?.instanceId === instanceId && !store.editors.signal.aborted;
  return {
    current,
    get drafts() { return memory(); },
    can: (capability: EditorCapability) => current() && store.can(capability),
    readDraft: () => drafts.get(key),
    keepDraft(draft: Record<string, unknown>): void {
      drafts.set(key, draft);
    },
    markDirty(edited: boolean): void {
      if (edited) dirty.add(key);
      else dirty.delete(key);
    },
    removeDraft(reason: 'cancel' | 'saved' | 'disposed' = 'saved'): void {
      memory().settle(reason);
      drafts.delete(key);
      dirty.delete(key);
    },
    readCatalog: (signal: AbortSignal) => client.read(route, notificationCatalog, signal),
    readCredentials(taskId: string, revision: number, signal: AbortSignal) {
      return client.read(route + '/' + taskId + '/credentials', (value) => {
        const row = objectValue(value), fields = secretStates(value);
        if (row.taskId !== taskId || integerValue(row.catalogRevision) < revision ||
            fields.length !== secretFields.length || new Set(fields.map((field) => field.field)).size !== secretFields.length)
          throw new Error('notification_credentials_invalid');
        return fields;
      }, signal);
    },
    save(target: NotificationTarget, input: ControlNotificationCommandInput, secrets: ControlNotificationCommandSecret[]) {
      return client.write(route + '/save', { ...command('save', target), input, secrets }, (value) => {
        const saved = notificationCatalog(value);
        const task = saved.tasks.find((item) => item.id === target.taskId);
        if (saved.revision !== target.expectedCatalogRevision + 1 || task?.revision !== target.expectedTaskRevision + 1)
          throw new TypeError('notification_save_result_unbound');
      }, target.operationId);
    },
    testSaved(target: NotificationTarget) {
      return client.write(route + '/test', command('test', target), (value) => {
        const accepted = objectValue(value);
        if (accepted.operationId !== target.operationId || accepted.taskId !== target.taskId || typeof accepted.isNew !== 'boolean')
          throw new TypeError('notification_test_result_unbound');
      }, target.operationId);
    },
    async checkOperation(id: string, action: EditorAction): Promise<boolean> {
      const operation = await client.read('/control/v1/operations/' + id, objectValue);
      const expected = action === 'save' ? 'notification.task.save' : 'notification.saved-test';
      if (operation.operationId !== id || operation.kind !== expected)
        throw Error('通知操作类型不匹配；草稿已保留。');
      return operation.status === 'completed';
    }
  };
}

export type NotificationEditorPort = ReturnType<typeof notificationEditorPort>;
