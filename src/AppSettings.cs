namespace NexusPipeline;

public class AppSettings
{
    public bool AutoStart { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool LightweightMode { get; set; }

    public bool AutoOpenBrowser { get; set; }

    public int HistoryRetentionDays { get; set; } = 3;

    public int WebPort { get; set; } = 58731;

    public string LogLevel { get; set; } = "info";

    public string SendStrategy { get; set; } = "parallel";

    public bool WebhookEnabled { get; set; } = true;

    public bool SmtpEnabled { get; set; }

    public string WebhookType { get; set; } = "feishu";

    public string WebhookUrl { get; set; } = "";

    public string WebhookSecret { get; set; } = "";

    public string WebhookTemplate { get; set; } = "";

    public int WebhookTimeout { get; set; } = 30;

    public string SmtpHost { get; set; } = "";

    public int SmtpPort { get; set; } = 465;

    public string SmtpSecure { get; set; } = "auto";

    public string SmtpUser { get; set; } = "";

    public string SmtpPassword { get; set; } = "";

    public string SmtpFrom { get; set; } = "";

    public string SmtpTo { get; set; } = "";

    public string SmtpSubjectPrefix { get; set; } = "[NexusPipeline]";

    public int SmtpTimeout { get; set; } = 30;

    /// <summary>启用的内置插件白名单（默认 notify）；外部插件默认启用，禁用记录在 <see cref="DisabledPlugins"/>。</summary>
    public List<string> EnabledPlugins { get; set; } = new() { "notify" };

    /// <summary>显式禁用的外部插件列表（持久化；删除后该插件重新默认启用）。</summary>
    public List<string> DisabledPlugins { get; set; } = new();
}
