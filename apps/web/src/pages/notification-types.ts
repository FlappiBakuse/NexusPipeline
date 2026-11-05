import type {
  ControlNotificationCommand,
  ControlNotificationCommandConfiguration,
  ControlNotificationCommandInput
} from '../control/wire.generated';
import { arrayValue, integerValue, objectValue, stringValue } from '../control/client';

export interface NotificationPageSession {
  pending: ControlNotificationCommand | null;
  busy: boolean;
  error: string;
  notice: string;
}

export interface NotificationTask extends ControlNotificationCommandInput {
  id: string;
  revision: number;
}
export interface NotificationCatalog {
  revision: number;
  maximumTasks: number;
  tasks: NotificationTask[];
}
export type NotificationChannel = 'smtp' | ControlNotificationCommandConfiguration['webhookKind'];
export const notificationChannels: readonly { id: NotificationChannel; label: string }[] = [
  { id: 'smtp', label: 'SMTP 邮件' },
  { id: 'feishu', label: '飞书 Webhook' },
  { id: 'wecom', label: '企业微信 Webhook' },
  { id: 'slack', label: 'Slack Webhook' },
  { id: 'discord', label: 'Discord Webhook' },
  { id: 'generic', label: '通用 Webhook' }
];
export function channelLabel(task: ControlNotificationCommandInput): string {
  const id = task.kind === 'smtp' ? 'smtp' : task.configuration.webhookKind;
  return notificationChannels.find((channel) => channel.id === id)?.label ?? id;
}
export type SecretField =
  | 'webhook-url'
  | 'webhook-secret'
  | 'feishu-app-secret'
  | 'slack-bot-token'
  | 'smtp-password';
export interface SecretState {
  field: SecretField;
  revision: number;
  isConfigured: boolean;
  isReadable: boolean;
}
export const secretFields: SecretField[] = [
  'webhook-url',
  'webhook-secret',
  'feishu-app-secret',
  'slack-bot-token',
  'smtp-password'
];

export function notificationCatalog(value: unknown): NotificationCatalog {
  const row = objectValue(value);
  return {
    revision: integerValue(row.revision),
    maximumTasks: integerValue(row.maximumTasks),
    tasks: arrayValue(row.tasks, (item) => {
      const task = objectValue(item);
      if (task.kind !== 'smtp' && task.kind !== 'webhook') throw new TypeError('notification_kind_invalid');
      return {
        id: stringValue(task.id),
        revision: integerValue(task.revision),
        name: stringValue(task.name),
        remark: stringValue(task.remark),
        kind: task.kind,
        isEnabled: task.isEnabled === true,
        configuration: objectValue(task.configuration) as unknown as ControlNotificationCommandConfiguration
      };
    })
  };
}
export function secretStates(value: unknown): SecretState[] {
  const row = objectValue(value);
  return arrayValue(row.fields, (item) => {
    const field = objectValue(item);
    if (!secretFields.includes(field.field as SecretField))
      throw new TypeError('notification_secret_field_invalid');
    return {
      field: field.field as SecretField,
      revision: integerValue(field.revision),
      isConfigured: field.isConfigured === true,
      isReadable: field.isReadable === true
    };
  });
}
export function newNotification(channel: NotificationChannel): ControlNotificationCommandInput {
  return {
    name: '',
    remark: '',
    kind: channel === 'smtp' ? 'smtp' : 'webhook',
    isEnabled: false,
    configuration: {
      screenshotEnabled: false,
      timeoutSeconds: 30,
      webhookKind: channel === 'smtp' ? 'feishu' : channel,
      webhookTemplate: '',
      feishuAppId: '',
      slackChannelId: '',
      smtpHost: '',
      smtpPort: 465,
      smtpSecurity: 'auto',
      smtpUser: '',
      smtpFrom: '',
      smtpTo: '',
      smtpSubjectPrefix: '[NexusPipeline]'
    }
  };
}
