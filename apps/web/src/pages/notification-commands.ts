import { createIdentity } from '../control/identity';
import { ControlFailure } from '../control/client';
import type { ControlNotificationCommandInput } from '../control/wire.generated';
import { copyDraft } from '../app/draft-copy';
import type { NotificationCredentials } from './notification-credentials';
import type { NotificationEditorPort } from './notification-editor-port';
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
    this.port.clearOperation(this.session.pending);
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
      this.port.report(reason);
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
      this.port.report(reason);
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
    } catch (reason) {
      this.port.report(reason);
      this.session.error = '无法确认原通知操作；草稿与操作 ID 已保留。';
    } finally {
      this.session.busy = false;
    }
  }
}
