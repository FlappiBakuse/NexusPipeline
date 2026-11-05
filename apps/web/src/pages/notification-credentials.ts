import { reactive } from 'vue';
import type { AppStore } from '../app/store';
import { integerValue, objectValue } from '../control/client';
import type {
  ControlNotificationCommandInput,
  ControlNotificationCommandSecret
} from '../control/wire.generated';
import { secretFields, secretStates, type SecretField, type SecretState } from './notification-types';

export class NotificationCredentials {
  readonly view = reactive({
    statuses: [] as SecretState[],
    ready: false,
    modes: Object.fromEntries(secretFields.map((field) => [field, 'keep'])) as Record<
      SecretField,
      'keep' | 'set' | 'clear'
    >,
    values: Object.fromEntries(secretFields.map((field) => [field, ''])) as Record<SecretField, string>
  });
  private readonly instance: string | undefined;
  private owner?: AbortController;
  private alive = true;

  constructor(
    private readonly store: AppStore,
    private readonly taskId: string | undefined,
    private readonly draft: () => ControlNotificationCommandInput,
    private readonly revision: () => number,
    private readonly failed: (message: string) => void
  ) {
    this.instance = store.state.capabilities?.instanceId;
    this.view.ready = !taskId;
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
    if (id !== this.taskId || !this.alive) return;
    this.owner?.abort();
    this.view.ready = false;
    const owner = new AbortController();
    this.owner = owner;
    try {
      const statuses = await this.store.client.read(
        '/control/v1/notifications/' + id + '/credentials',
        (value) => {
          const row = objectValue(value),
            fields = secretStates(value);
          if (
            row.taskId !== id ||
            integerValue(row.catalogRevision) < this.revision() ||
            fields.length !== secretFields.length ||
            new Set(fields.map((field) => field.field)).size !== secretFields.length
          )
            throw new Error('notification_credentials_invalid');
          return fields;
        },
        owner.signal
      );
      owner.signal.throwIfAborted();
      if (this.alive && this.instance === this.store.state.capabilities?.instanceId) {
        this.view.statuses = statuses;
        this.view.ready = true;
      }
    } catch (error) {
      if (!owner.signal.aborted && this.alive) {
        this.store.report(error);
        this.failed(error instanceof Error ? error.message : '凭据状态读取失败');
      }
    }
  }

  edit(field: SecretField, value: string): void {
    this.view.values[field] = value;
    this.view.modes[field] = value ? 'set' : 'keep';
  }

  retain(fields: readonly SecretField[]): void {
    for (const field of secretFields)
      if (!fields.includes(field)) {
        this.view.values[field] = '';
        this.view.modes[field] = 'keep';
      }
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
    this.clear();
  }
}
