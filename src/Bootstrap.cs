using System.Net;
using NexusPipeline.Web;

namespace NexusPipeline;

/// <summary>服务启动/停止编排：插件、历史清理、调度器、Web 服务（端口重试）。</summary>
internal static class Bootstrap
{
    /// <summary>加载插件、清理过期历史、启动调度器。</summary>
    public static void StartServices()
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.Plugins.LoadAll();
        ctx.History.Cleanup(ctx.Settings.HistoryRetentionDays);
        ctx.Scheduler.Start();
    }

    /// <summary>启动 Web 服务：端口被占用自动 +1 重试（最多 20 次）。失败返回 null。</summary>
    public static WebServer? StartWebWithRetry(int basePort)
    {
        var web = new WebServer();
        int port = basePort;
        bool started = false;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                web.Start(port);
                started = true;
                break;
            }
            catch (HttpListenerException)
            {
                Logger.Warn($"[提示] 端口 {port} 被占用，尝试 {port + 1}。");
                port++;
            }
        }
        if (!started)
        {
            Logger.Error("[错误] 无法启动 Web 服务（端口均被占用）。");
            return null;
        }
        return web;
    }

    /// <summary>停止调度器、Web 服务与全部插件。</summary>
    public static void Shutdown(WebServer? web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.Scheduler.Stop();
        web?.Stop();
        ctx.Plugins.ShutdownAll();
    }
}
