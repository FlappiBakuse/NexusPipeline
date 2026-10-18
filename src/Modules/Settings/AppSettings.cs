using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Shared.Logging;
namespace NexusPipeline.Modules.Settings;

using System.Text.Json;
using System.Text.Json.Serialization;

public class AppSettings
{
    public bool AutoStart { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool LightweightMode { get; set; }

    /// <summary>User-initiated specialized configuration repair is opt-in and disabled by default.</summary>
    public bool AllowConfigRepair { get; set; }

    public bool OpenDesktopOnStartup { get; set; }

    public int HistoryRetentionDays { get; set; } = 7;

    public int WebPort { get; set; } = 58731;

    /// <summary>是否启动内嵌 MCP Streamable HTTP 端点；默认关闭。</summary>
    public bool McpEnabled { get; set; }

    public bool McpWriteEnabled { get; set; }

    /// <summary>MCP 仅绑定 loopback 的固定监听端口；端口冲突时不自动漂移。</summary>
    public int McpPort { get; set; } = 58732;

    public string LogLevel { get; set; } = "info";

    /// <summary>宿主后台输出、CLI、托盘和通知使用的全局语言；Web 页面语言由浏览器偏好决定。</summary>
    public string HostLocale { get; set; } = "zh-CN";

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

    /// <summary>是否启用定期自动检查更新；服务启动约 5 秒后首次检查，此后每 12 小时检查一次。</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>发现更新后，宿主进入闲时并满足调度安全窗口时自动应用并重启。</summary>
    public bool UpdateAutoApplyEnabled { get; set; }

    /// <summary>启动时检查并在加载插件前下载更新；运行期间定期下载，并在闲时安全重启时应用。</summary>
    public bool PluginAutoUpdateEnabled { get; set; }

    /// <summary>更新接受渠道：stable / prerelease（major=0 版本为 Pre-release，默认 prerelease 才能收到更新）。</summary>
    public string UpdateChannel { get; set; } = "prerelease";

    /// <summary>可选更新镜像源（GitHub Releases API JSON）；空 = 默认 GitHub。</summary>
    public string UpdateSourceUrl { get; set; } = "";

    [JsonPropertyName("pluginRepository")]
    public PluginRepositorySettings PluginRepository { get; set; } = new();

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
