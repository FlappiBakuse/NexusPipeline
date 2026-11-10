import type { AppStore } from '../app/store';
import { integerValue, objectValue } from '../control/client';
import type {
  ControlNotificationCommand,
  ControlNotificationCommandInput,
  ControlNotificationCommandSecret
} from '../control/wire.generated';
import { notificationCatalog, secretFields, secretStates } from './notification-types';

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
  store: Pick<AppStore, 'state' | 'client' | 'can' | 'report' | 'drafts' | 'dirty'>,
  instanceId: string,
  draftName: string
) {
  const { client, state, drafts, dirty } = store;
  const key = 'notification/editor/' + instanceId + '/' + draftName;
  const current = () => state.capabilities?.instanceId === instanceId;
  return {
    current,
    can: (capability: EditorCapability) => current() && store.can(capability),
    readDraft: () => drafts.get(key),
    keepDraft(draft: Record<string, unknown>): void {
      drafts.set(key, draft);
    },
    markDirty(edited: boolean): void {
      if (edited) dirty.add(key);
      else dirty.delete(key);
    },
    removeDraft(): void {
      drafts.delete(key);
      dirty.delete(key);
    },
    report(reason: unknown): void {
      if (current()) store.report(reason);
    },
    clearError(message: string): void {
      if (current() && state.error === message) state.error = '';
    },
    clearOperation(id: string): void {
      if (id && current() && state.uncertainOperation === id) {
        state.uncertainOperation = '';
        state.error = '';
      }
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
