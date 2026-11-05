import { createIdentity } from '../control/identity';
import type { AppStore } from '../app/store';
import { ControlFailure, objectValue } from '../control/client';
import type { ControlNotificationCommand, ControlNotificationCommandInput } from '../control/wire.generated';
import { copyDraft } from '../app/draft-copy';
import { notificationCatalog } from './notification-types';
import { NotificationCredentials } from './notification-credentials';
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
  private readonly instance: string | undefined;
  constructor(
    private readonly store: AppStore,
    private readonly session: NotificationSession,
    private readonly credentials: NotificationCredentials,
    private readonly key: string,
    private readonly existing: boolean,
    private readonly validate: () => boolean
  ) {
    this.instance = store.state.capabilities?.instanceId;
  }
  private current(): boolean {
    return this.instance === this.store.state.capabilities?.instanceId;
  }
  private dirty(): boolean {
    return JSON.stringify(this.session.draft) !== this.session.baseline || this.credentials.dirty;
  }
  private clearPending(): void {
    if (this.current() && this.store.state.uncertainOperation === this.session.pending) {
      this.store.state.uncertainOperation = '';
      this.store.state.error = '';
    }
    this.session.pending = '';
    this.session.pendingAction = '';
  }
  private finishSaved(): void {
    if (!this.current()) return;
    this.credentials.clear();
    this.session.baseline = JSON.stringify(this.session.draft);
    this.clearPending();
    this.store.drafts.delete(this.key);
    this.store.dirty.delete(this.key);
    this.session.completed = true;
  }
  async save(): Promise<void> {
    if (
      !this.current() ||
      this.session.busy ||
      this.session.pending ||
      this.session.conflict ||
      !this.credentials.view.ready ||
      !this.store.can('notifications.write')
    )
      return;
    if (!this.validate()) return;
    this.session.busy = true;
    this.session.error = '';
    this.session.notice = '';
    const id = createIdentity();
    try {
      const command: ControlNotificationCommand = {
        format: 'nexus.control.notification-command',
        version: 1,
        action: 'save',
        operationId: id,
        expectedCatalogRevision: this.session.catalogRevision,
        taskId: this.session.taskId,
        expectedTaskRevision: this.session.taskRevision,
        input: copyDraft(this.session.draft),
        secrets: this.credentials.edits()
      };
      this.session.pending = id;
      this.session.pendingAction = 'save';
      await this.store.client.write(
        '/control/v1/notifications/save',
        command,
        (value) => {
          const saved = notificationCatalog(value);
          const task = saved.tasks.find((item) => item.id === this.session.taskId);
          if (
            saved.revision !== this.session.catalogRevision + 1 ||
            task?.revision !== this.session.taskRevision + 1
          )
            throw new TypeError('notification_save_result_unbound');
          return saved;
        },
        id
      );
      this.finishSaved();
    } catch (reason) {
      this.store.report(reason);
      this.session.error = reason instanceof Error ? reason.message : '保存失败；草稿已保留';
      if (reason instanceof ControlFailure && reason.status === 409) this.session.conflict = true;
      if (!(reason instanceof ControlFailure && reason.uncertain)) {
        this.session.pending = '';
        this.session.pendingAction = '';
      }
    } finally {
      this.session.busy = false;
    }
  }
  async test(): Promise<void> {
    if (
      !this.current() ||
      !this.existing ||
      this.session.busy ||
      this.session.pending ||
      this.dirty() ||
      !this.store.can('notifications.test')
    ) {
      this.session.notice = '先保存当前修改，再测试已保存的任务。';
      return;
    }
    this.session.busy = true;
    this.session.error = '';
    const id = createIdentity();
    try {
      const command: ControlNotificationCommand = {
        format: 'nexus.control.notification-command',
        version: 1,
        action: 'test',
        operationId: id,
        expectedCatalogRevision: this.session.catalogRevision,
        taskId: this.session.taskId,
        expectedTaskRevision: this.session.taskRevision
      };
      this.session.pending = id;
      this.session.pendingAction = 'test';
      await this.store.client.write(
        '/control/v1/notifications/test',
        command,
        (value) => {
          const accepted = objectValue(value);
          if (
            accepted.operationId !== id ||
            accepted.taskId !== this.session.taskId ||
            typeof accepted.isNew !== 'boolean'
          )
            throw new TypeError('notification_test_result_unbound');
          return accepted;
        },
        id
      );
      this.session.notice = '已将已保存任务送入测试队列。';
      this.clearPending();
    } catch (reason) {
      this.store.report(reason);
      this.session.error = reason instanceof Error ? reason.message : '测试请求失败';
      if (!(reason instanceof ControlFailure && reason.uncertain)) {
        this.session.pending = '';
        this.session.pendingAction = '';
      }
    } finally {
      this.session.busy = false;
    }
  }
  async checkPending(): Promise<void> {
    if (!this.current() || !this.session.pending || this.session.busy || !this.store.can('operations.read'))
      return;
    this.session.busy = true;
    try {
      const operation = await this.store.client.read(
        '/control/v1/operations/' + this.session.pending,
        objectValue
      );
      const expected =
        this.session.pendingAction === 'save' ? 'notification.task.save' : 'notification.saved-test';
      if (operation.operationId !== this.session.pending || operation.kind !== expected)
        throw Error('通知操作类型不匹配；草稿已保留。');
      if (operation.status === 'completed') {
        const action = this.session.pendingAction;
        if (action === 'save') this.finishSaved();
        else {
          this.clearPending();
          this.session.notice = '已确认测试请求送入队列。';
          this.session.error = '';
        }
      } else this.session.notice = '通知操作仍在处理，请稍后查询。';
    } catch (reason) {
      this.store.report(reason);
      this.session.error = '无法确认原通知操作；草稿与操作 ID 已保留。';
    } finally {
      this.session.busy = false;
    }
  }
}
