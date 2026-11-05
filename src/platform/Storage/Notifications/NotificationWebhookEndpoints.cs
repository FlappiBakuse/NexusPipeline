namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationWebhookEndpoints
{
    internal static NotificationWebhookEndpoints Production { get; } = new(
        new("https://open.feishu.cn/open-apis/"), new("https://slack.com/api/"));
    internal Uri Feishu { get; }
    internal Uri Slack { get; }
    private NotificationWebhookEndpoints(Uri feishu, Uri slack) { Feishu = feishu; Slack = slack; }

    internal static NotificationWebhookEndpoints LocalSink(Uri origin)
    {
        NotificationWebhookHttp.RequireTarget(origin);
        if (!origin.IsLoopback || origin.AbsolutePath != "/" || origin.Query.Length != 0)
            throw new ArgumentException("notification_sink_must_be_loopback", nameof(origin));
        return new(new(origin, "feishu/"), new(origin, "slack/"));
    }
}
