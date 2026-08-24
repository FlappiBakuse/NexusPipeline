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

    /// <summary>
    /// 插件用户偏好。缺少记录时，数据化专项插件默认启用，managed-code 插件默认禁用；
    /// 未发现插件的记录继续保留，插件重新安装后仍能恢复用户选择。
    /// </summary>
    public Dictionary<string, PluginPreference> PluginPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>生成与当前设置完全脱钩的候选对象，供设置 PUT 的 clone-on-write 事务使用。</summary>
    public AppSettings Clone()
    {
        return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();
    }
}

public sealed class PluginPreference
{
    public bool Enabled { get; set; }
}
