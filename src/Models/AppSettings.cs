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

    /// <summary>是否启动内嵌 MCP Streamable HTTP 端点；默认关闭。</summary>
    public bool McpEnabled { get; set; }

    /// <summary>MCP 仅绑定 loopback 的固定监听端口；端口冲突时不自动漂移。</summary>
    public int McpPort { get; set; } = 58732;

    public string LogLevel { get; set; } = "info";

    /// <summary>允许远程访问（绑定 http.sys 强通配符 +，非 0.0.0.0——http.sys 不接受 0.0.0.0 前缀；远程请求需访问令牌；默认仅本地 127.0.0.1）。</summary>
    public bool AllowRemoteAccess { get; set; }

    /// <summary>远程访问令牌（DPAPI 加密存储 enc: 前缀；本地请求豁免校验）。</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>宿主外部 HTTP 请求代理模式：none / system / http。</summary>
    public string ProxyMode { get; set; } = "none";

    /// <summary>自定义 HTTP/HTTPS 代理地址；仅 ProxyMode=http 时使用。</summary>
    public string ProxyUrl { get; set; } = "";

    /// <summary>自定义代理用户名，可选。</summary>
    public string ProxyUsername { get; set; } = "";

    /// <summary>自定义代理密码（DPAPI 加密存储）。</summary>
    public string ProxyPassword { get; set; } = "";

    public bool WebhookEnabled { get; set; } = true;

    /// <summary>脚本完成通知是否尝试附带判断脚本选择的截图；队列汇总通知不附图。</summary>
    public bool WebhookScreenshotEnabled { get; set; }

    public bool SmtpEnabled { get; set; }

    /// <summary>脚本完成邮件是否附带判断脚本选择的截图；队列汇总通知不附图。</summary>
    public bool SmtpScreenshotEnabled { get; set; }

    public string WebhookType { get; set; } = "feishu";

    /// <summary>Webhook 类型白名单（单源化）：ConfigStore.Normalize 校验与 WebhookSender 状态/映射共用，避免双份维护漂移。</summary>
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

    /// <summary>启动时自动检查一次更新（仅检查不下载）。</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>更新接受渠道：stable / prerelease（前项目全是 Pre-release，默认 prerelease 才能收到更新）。</summary>
    public string UpdateChannel { get; set; } = "prerelease";

    /// <summary>可选更新镜像源（GitHub Releases API JSON）；空 = 默认 GitHub。</summary>
    public string UpdateSourceUrl { get; set; } = "";

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
