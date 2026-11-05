namespace NexusPipeline.Core.Secrets;

public enum NotificationSecretField { WebhookUrl, WebhookSecret, FeishuAppSecret, SlackBotToken, SmtpPassword }

public static class NotificationSecretFields
{
    public static string Name(NotificationSecretField field) => field switch
    {
        NotificationSecretField.WebhookUrl => "webhook-url",
        NotificationSecretField.WebhookSecret => "webhook-secret",
        NotificationSecretField.FeishuAppSecret => "feishu-app-secret",
        NotificationSecretField.SlackBotToken => "slack-bot-token",
        NotificationSecretField.SmtpPassword => "smtp-password",
        _ => throw new ArgumentException("notification_secret_field_invalid", nameof(field))
    };

    public static NotificationSecretField Parse(string name) => name switch
    {
        "webhook-url" => NotificationSecretField.WebhookUrl,
        "webhook-secret" => NotificationSecretField.WebhookSecret,
        "feishu-app-secret" => NotificationSecretField.FeishuAppSecret,
        "slack-bot-token" => NotificationSecretField.SlackBotToken,
        "smtp-password" => NotificationSecretField.SmtpPassword,
        _ => throw new ArgumentException("notification_secret_field_invalid", nameof(name))
    };
}
