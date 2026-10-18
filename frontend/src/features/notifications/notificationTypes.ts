export type SecretAction = { action: "keep" | "replace" | "clear"; value?: string };
export interface NotificationDraft {
  name: string;
  remark: string;
  type: "smtp" | "webhook";
  enabled: boolean;
  configuration: Record<string, string | number | boolean>;
  secrets: Record<string, SecretAction>;
}
export interface NotificationTask extends Omit<NotificationDraft, "secrets"> {
  id: string;
  configured: boolean;
  configurationError?: string;
  secrets: Record<string, boolean>;
}
export function newNotification(type: "smtp" | "webhook"): NotificationDraft {
  return { name: "", remark: "", type, enabled: false, secrets: {}, configuration: {
    screenshotEnabled: false, timeout: 30, webhookType: "feishu", webhookUrl: "", webhookSecret: "",
    webhookTemplate: "", feishuAppId: "", feishuAppSecret: "", slackBotToken: "", slackChannelId: "",
    smtpHost: "", smtpPort: 465, smtpSecure: "auto", smtpUser: "", smtpPassword: "", smtpFrom: "",
    smtpTo: "", smtpSubjectPrefix: "[NexusPipeline]",
  } };
}
