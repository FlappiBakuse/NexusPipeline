using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationReadiness
{
    internal static bool Configured(NotificationTaskDefinition task, Func<NotificationSecretField, bool> hasSecret)
    {
        var config = task.Configuration;
        return task.Kind switch
        {
            NotificationTaskKind.Smtp => !string.IsNullOrWhiteSpace(config.SmtpHost) && !string.IsNullOrWhiteSpace(config.SmtpUser) &&
                !string.IsNullOrWhiteSpace(config.SmtpTo) && hasSecret(NotificationSecretField.SmtpPassword),
            NotificationTaskKind.Webhook => hasSecret(NotificationSecretField.WebhookUrl) &&
                (config.WebhookKind != NotificationWebhookKind.Generic || !string.IsNullOrWhiteSpace(config.WebhookTemplate)),
            _ => false
        };
    }
}
