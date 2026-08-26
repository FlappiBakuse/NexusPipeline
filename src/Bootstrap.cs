using System.Net;
using NexusPipeline.Web;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

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

    internal static bool CanStopServices(out string reason)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (ctx.Center.Active.Count > 0)
        {
            reason = "存在运行中的任务，请先等待完成或取消任务";
            return false;
        }
        if (UserConfigManager.EditSessions.Count > 0)
        {
            reason = "存在编辑配置会话，请先完成或取消编辑";
            return false;
        }
        reason = "";
        return true;
    }

    internal static bool CanRequestDirectExit(out string reason)
    {
        if (!CanStopServices(out reason))
        {
            return false;
        }
        if (RuntimeContext.Instance.Center.CurrentSystemAction is not null)
        {
            reason = "存在待执行的系统操作，请先完成或取消该操作";
            return false;
        }
        return true;
    }

    internal static (HostMaintenanceLease? Lease, string? Reason) TryAcquireUpdateMaintenanceLease()
    {
        HostMaintenanceLease? lease = RuntimeContext.Instance.Center.TryAcquireMaintenanceLease(out string reason);
        return (lease, lease is null ? reason : null);
    }

    internal static bool TryRequestDirectExit()
    {
        if (CanRequestDirectExit(out string reason))
        {
            System.Windows.Forms.Application.Exit();
            return true;
        }
        Logger.Warn($"[退出] 已拒绝退出请求：{reason}");
        try
        {
            System.Windows.Forms.MessageBox.Show(reason, "NexusPipeline", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
        }
        catch
        {
        }
        return false;
    }

    internal static bool TryRequestCompletionExit()
    {
        if (!CanStopServices(out string reason))
        {
            Logger.Warn($"[退出] 完成操作退出请求被延后：{reason}");
            return false;
        }
        System.Windows.Forms.Application.Exit();
        return true;
    }

    /// <summary>
    /// 更新应用后的宿主退出：常驻服务走完成操作退出门禁；
    /// web 模式没有 WinForms 消息循环（Application.Exit 无效），直接延时退出进程——单实例互斥体随进程终止释放，
    /// apply-update 子进程接管切换。
    /// </summary>
    internal static bool TryRequestUpdateExit()
    {
        if (ApplicationHost.IsWebOnly)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TestHooks.ScaledMs(1500)).ConfigureAwait(false);
                Environment.Exit(0);
            });
            return true;
        }
        return TryRequestCompletionExit();
    }

    /// <summary>停止调度器、配置恢复重试、Web 服务与全部插件；分步保护：单步异常不影响其余清理步骤执行。</summary>
    public static void Shutdown(WebServer? web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (!CanStopServices(out string reason))
        {
            Logger.Warn($"[退出] 服务仍有活动任务，拒绝执行宿主停止：{reason}");
            return;
        }
        try
        {
            ctx.Scheduler.Stop();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 调度器停止异常：{ex.Message}");
        }
        try
        {
            UserConfigManager.StopRecoveryRetry();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 配置恢复重试停止异常：{ex.Message}");
        }
        try
        {
            web?.Stop();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] Web 服务停止异常：{ex.Message}");
        }
        try
        {
            ctx.Plugins.ShutdownAll();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 插件停止异常：{ex.Message}");
        }
    }
}
