using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Modules.Notifications;

internal sealed class NotificationTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Type { get; set; } = "smtp";
    public bool Enabled { get; set; }
    public NotificationChannelSettings Configuration { get; set; } = new();

    public NotificationTask Clone() => JsonSerializer.Deserialize<NotificationTask>(JsonSerializer.Serialize(this))!;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class NotificationTaskStore
{
    [JsonRequired]
    public int SchemaVersion { get; set; } = 1;
    public List<NotificationTask> Tasks { get; set; } = [];
}

internal sealed class NotificationTaskInput
{
    public string Name { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Type { get; set; } = "smtp";
    public bool Enabled { get; set; }
    public NotificationChannelSettings Configuration { get; set; } = new();
    public Dictionary<string, NotificationSecretInput> Secrets { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record NotificationSecretInput(string Action, string? Value = null);

internal sealed record NotificationTaskView(
    string Id, string Name, string Remark, string Type, bool Enabled,
    bool Configured, string? ConfigurationError, NotificationChannelSettings Configuration,
    IReadOnlyDictionary<string, bool> Secrets);

internal sealed class NotificationChannelSettings
{
    public bool ScreenshotEnabled { get; set; }
    public int Timeout { get; set; } = 30;
    public string WebhookType { get; set; } = "feishu";
    public string WebhookUrl { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string WebhookTemplate { get; set; } = "";
    public string FeishuAppId { get; set; } = "";
    public string FeishuAppSecret { get; set; } = "";
    public string SlackBotToken { get; set; } = "";
    public string SlackChannelId { get; set; } = "";
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public string SmtpSecure { get; set; } = "auto";
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string SmtpFrom { get; set; } = "";
    public string SmtpTo { get; set; } = "";
    public string SmtpSubjectPrefix { get; set; } = "[NexusPipeline]";

    public NotificationChannelSettings Clone() => JsonSerializer.Deserialize<NotificationChannelSettings>(JsonSerializer.Serialize(this))!;

    internal bool HasNonNullFields() => new[] { WebhookType, WebhookUrl, WebhookSecret, WebhookTemplate,
        FeishuAppId, FeishuAppSecret, SlackBotToken, SlackChannelId, SmtpHost, SmtpSecure, SmtpUser,
        SmtpPassword, SmtpFrom, SmtpTo, SmtpSubjectPrefix }.All(value => value is not null);

    internal static readonly string[] WebhookTypes = ["feishu", "wecom", "slack", "discord", "generic"];
    internal static readonly string[] SecretNames = ["webhookUrl", "webhookSecret", "feishuAppSecret", "slackBotToken", "smtpPassword"];

    internal string GetSecret(string name) => name switch
    {
        "webhookUrl" => WebhookUrl, "webhookSecret" => WebhookSecret,
        "feishuAppSecret" => FeishuAppSecret, "slackBotToken" => SlackBotToken,
        "smtpPassword" => SmtpPassword, _ => throw new ArgumentException("Unknown notification secret")
    };

    internal void SetSecret(string name, string value)
    {
        switch (name)
        {
            case "webhookUrl": WebhookUrl = value; break;
            case "webhookSecret": WebhookSecret = value; break;
            case "feishuAppSecret": FeishuAppSecret = value; break;
            case "slackBotToken": SlackBotToken = value; break;
            case "smtpPassword": SmtpPassword = value; break;
            default: throw new ArgumentException("Unknown notification secret");
        }
    }
}
