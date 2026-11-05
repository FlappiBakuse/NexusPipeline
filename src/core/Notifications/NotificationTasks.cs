using System.Collections.Immutable;

namespace NexusPipeline.Core.Notifications;

public enum NotificationTaskKind { Smtp, Webhook }
public enum NotificationWebhookKind { Feishu, Wecom, Slack, Discord, Generic }
public enum NotificationSmtpSecurity { Auto, Ssl, StartTls, None }

public sealed record NotificationChannelConfiguration(bool ScreenshotEnabled, int TimeoutSeconds,
    NotificationWebhookKind WebhookKind, string WebhookTemplate, string FeishuAppId, string SlackChannelId,
    string SmtpHost, int SmtpPort, NotificationSmtpSecurity SmtpSecurity, string SmtpUser,
    string SmtpFrom, string SmtpTo, string SmtpSubjectPrefix)
{
    public static NotificationChannelConfiguration Default { get; } = new(false, 30, NotificationWebhookKind.Feishu,
        "", "", "", "", 465, NotificationSmtpSecurity.Auto, "", "", "", "[NexusPipeline]");
}

public sealed record NotificationTaskDefinition(Guid Id, long Revision, string Name, string Remark,
    NotificationTaskKind Kind, bool IsEnabled, NotificationChannelConfiguration Configuration);
public sealed record NotificationTaskCatalog(long Revision, int MaximumTasks, ImmutableArray<NotificationTaskDefinition> Tasks)
{
    public static NotificationTaskCatalog Empty { get; } = new(0, 10, []);
}

public sealed record NotificationTaskInput(string Name, string Remark, NotificationTaskKind Kind,
    bool IsEnabled, NotificationChannelConfiguration Configuration);
public sealed record NotificationTargetState(Guid TaskId, long Revision, string Name, bool IsEnabled, bool IsConfigured, NotificationTaskKind Kind);
public sealed record NotificationTargetCatalog(long Revision, ImmutableArray<NotificationTargetState> Tasks);
public sealed record NotificationTaskSelection(ImmutableArray<Guid> TaskIds);
public enum NotificationRoutingKind { Binding, Queue, ScheduledInvocation }
public sealed record NotificationRoute(NotificationRoutingKind Kind, Guid SourceId, NotificationTaskSelection Selection);

public sealed class NotificationTaskException(string code) : Exception(code);
