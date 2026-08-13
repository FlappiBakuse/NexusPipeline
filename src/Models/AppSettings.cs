namespace NexusPipeline.Models;

public class AppSettings
{
    public bool AutoStart { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool LightweightMode { get; set; }

    public bool AutoOpenBrowser { get; set; }

    public int HistoryRetentionDays { get; set; } = 7;

    public int WebPort { get; set; } = 58731;

    public string LogLevel { get; set; } = "info";

    /// <summary>允许远程访问（绑定 http.sys 强通配符 +，非 0.0.0.0——http.sys 不接受 0.0.0.0 前缀；远程请求需访问令牌；默认仅本地 127.0.0.1）。</summary>
    public bool AllowRemoteAccess { get; set; }

    /// <summary>远程访问令牌（DPAPI 加密存储 enc: 前缀；本地请求豁免校验）。</summary>
    public string AccessToken { get; set; } = "";

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
