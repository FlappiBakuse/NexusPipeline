using System.Net;
using NexusPipeline.Web;

namespace NexusPipeline;

/// <summary>服务启动/停止编排：插件、历史清理、调度器、Web 服务（端口重试）。</summary>
internal static class Bootstrap
{
    /// <summary>加载插件、清理过期历史、启动调度器与配置恢复重试。</summary>
    public static void StartServices()
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.Plugins.LoadAll();
        ctx.History.Cleanup(ctx.Settings.HistoryRetentionDays);
        ctx.Scheduler.Start();
        UserConfigManager.StartRecoveryRetry();
    }

    /// <summary>启动 Web 服务：端口被占用自动 +1 重试（最多 20 次）。每次重试新建实例（HttpListener Start 失败后不可复用，否则抛 ObjectDisposedException 导致进程崩溃）；非端口冲突异常直接返回 null（不崩溃）。失败返回 null。</summary>
    public static WebServer? StartWebWithRetry(int basePort)
    {
        int port = basePort;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var web = new WebServer();
                web.Start(port);
                return web;
            }
            catch (HttpListenerException)
            {
                Logger.Warn($"[提示] 端口 {port} 被占用，尝试 {port + 1}。");
                port++;
            }
            catch (Exception ex)
            {
                Logger.Error($"[错误] Web 服务启动异常：{ex.Message}");
                return null;
            }
        }
        Logger.Error("[错误] 无法启动 Web 服务（端口均被占用）。");
        return null;
    }

    /// <summary>Web 服务启动成功后的收尾：远程访问模式确保防火墙入站规则存在。</summary>
    public static void AfterWebStarted(WebServer web)
    {
        if (RuntimeContext.Instance.Settings.AllowRemoteAccess)
        {
            FirewallRule.EnsureAllowInbound();
        }
    }

    /// <summary>停止调度器、配置恢复重试、Web 服务与全部插件。</summary>
    public static void Shutdown(WebServer? web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.Scheduler.Stop();
        UserConfigManager.StopRecoveryRetry();
        web?.Stop();
        ctx.Plugins.ShutdownAll();
    }
}
