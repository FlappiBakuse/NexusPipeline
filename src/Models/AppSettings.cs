namespace NexusPipeline.Models;

using System.Text.Json;

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

    public bool WebhookEnabled { get; set; } = true;

    public bool SmtpEnabled { get; set; }

    public string WebhookType { get; set; } = "feishu";

    /// <summary>Webhook 类型白名单（v0.7.4 单源化，KN-26）：ConfigStore.Normalize 校验与 WebhookSender 状态/映射共用，避免双份维护漂移。</summary>
    public static readonly string[] WebhookTypes = { "feishu", "dingtalk", "wecom", "slack", "discord", "generic" };

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

    /// <summary>模拟器适配内置插件名（v0.7.0+）：禁用后模拟器启动方式不可用。</summary>
    public const string EmulatorAdapterPlugin = "emulator-adapter";

    /// <summary>启用的内置插件白名单（默认 notify + 模拟器适配）；外部插件默认启用，禁用记录在 <see cref="DisabledPlugins"/>。</summary>
    public List<string> EnabledPlugins { get; set; } = new() { "notify", EmulatorAdapterPlugin };

    /// <summary>显式禁用的外部插件列表（持久化；删除后该插件重新默认启用）。</summary>
    public List<string> DisabledPlugins { get; set; } = new();

    /// <summary>生成与当前设置完全脱钩的候选对象，供设置 PUT 的 clone-on-write 事务使用。</summary>
    public AppSettings Clone()
    {
        return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();
    }
}
