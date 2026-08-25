using NexusPipeline.Cli;
using NexusPipeline.Services;
using NexusPipeline.Utilities;
using NexusPipeline.Web;

namespace NexusPipeline;

internal static class StartupPipeline
{
    internal static void RunService()
    {
        using Mutex? mutex = AcquireSingleInstanceMutex();
        if (mutex is null)
        {
            Logger.Info("检测到 NexusPipeline 已在运行，本次启动退出（可在托盘图标打开管理页面）。");
            TrayApp.OpenWeb();
            return;
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.ReloadSettings();
        ctx.ReloadData();
        // v0.6.6+：崩溃恢复仅常驻服务执行（manage/web/CLI 由运行时自愈 RecoverIfNeeded 兜底），避免多进程并发恢复竞争文件。
        UserConfigManager.RecoverInterrupted(ctx.SnapshotUsers());
        TaskRegistration.SyncWithSettings(ctx.Settings);
        Bootstrap.StartServices();

        WebServer? web = null;
        if (!ctx.Settings.LightweightMode)
        {
            web = Bootstrap.StartWebWithRetry(ctx.Settings.WebPort);
            if (web is not null)
            {
                Bootstrap.AfterWebStarted(web);
                if (ctx.Settings.AutoOpenBrowser)
                {
                    TrayApp.OpenWeb(web.Port);
                }
            }
        }
        else
        {
            Logger.Info("轻量运行模式：不启动 Web 服务与浏览器，仅命令行操作。");
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());

        WaitForSafeShutdown();
        Bootstrap.Shutdown(web);
        Logger.Info("NexusPipeline 已退出。");
    }

    /// <summary>
    /// 创建单实例互斥体并取得所有权（v0.6.5+）：处理「服务被强杀后互斥体被遗弃」——构造函数会抛
    /// AbandonedMutexException（所有权已授予本线程），此时先打开同一互斥体释放遗弃所有权再重试一次，
    /// 避免强杀后首次启动即崩溃（曾需启动两次）。已有实例在运行时返回 null。
    /// </summary>
    internal static Mutex? AcquireSingleInstanceMutex()
    {
        const string name = "NexusPipeline.SingleInstance";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var mutex = new Mutex(true, name, out bool createdNew);
                if (createdNew)
                {
                    return mutex;
                }
                mutex.Dispose();
                return null;
            }
            catch (AbandonedMutexException ex)
            {
                Logger.Warn($"[警告] 接管上次异常退出残留的单实例互斥体（{ex.Message}），正在重试启动...");
                try
                {
                    using var stale = new Mutex(false, name);
                    stale.ReleaseMutex();
                }
                catch (Exception e)
                {
                    Logger.Debug($"释放遗弃互斥体失败：{e.Message}");
                }
            }
        }
        Logger.Error("[错误] 获取单实例互斥体失败（两次尝试均被遗弃状态占用）。");
        return null;
    }

    /// <summary>自动重启分支（v0.6.5+）：等待旧进程释放单实例互斥体（旧进程收到退出指令后 ~1 秒退出并释放，
    /// 强杀残留的遗弃互斥体视为已获得），随后进入常驻服务模式。</summary>
    internal static int RunRestart()
    {
        Logger.Info("[重启] 正在等待旧进程退出...");
        try
        {
            using var probe = new Mutex(false, "NexusPipeline.SingleInstance");
            DateTime deadline = DateTime.Now.AddSeconds(30);
            while (DateTime.Now < deadline)
            {
                try
                {
                    if (probe.WaitOne(500))
                    {
                        try
                        {
                            probe.ReleaseMutex();
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"释放互斥体失败：{ex.Message}");
                        }
                        break;
                    }
                }
                catch (AbandonedMutexException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[重启] 等待旧进程退出异常（继续启动）：{ex.Message}");
        }
        RunService();
        return 0;
    }

    internal static int RunWebOnly(string[] args)
    {
        // v0.6.6+：web 模式同样抢单实例互斥——常驻服务已在运行时直接退出（防两实例双写配置/数据）。
        using Mutex? mutex = AcquireSingleInstanceMutex();
        if (mutex is null)
        {
            int? existingPort = CliTransport.FindServicePort(RuntimeContext.Instance.Settings.WebPort);
            if (existingPort is not null)
            {
                Logger.Info($"检测到已有 NexusPipeline 服务，复用 Web 端口 {existingPort.Value}。");
                TrayApp.OpenWeb(existingPort.Value);
                return 0;
            }
            Logger.Warn("[错误] 检测到 NexusPipeline 已在运行，但未能发现其 Web 端口；本次网页模式退出。");
            Console.WriteLine("[错误] 检测到已有 NexusPipeline 服务，但无法发现 Web 端口，请查看服务日志。");
            return 1;
        }
        ApplicationHost.IsWebOnly = true;
        RuntimeContext ctx = RuntimeContext.Instance;
        // v0.6.6+：崩溃恢复仅服务类进程执行（service/web 均含调度与配置交换能力；manage/status/CLI 由运行时自愈兜底）。
        UserConfigManager.RecoverInterrupted(ctx.SnapshotUsers());
        Bootstrap.StartServices();
        WebServer? web = Bootstrap.StartWebWithRetry(ctx.Settings.WebPort);
        if (web is null)
        {
            Console.WriteLine("[错误] 无法启动 Web 服务（端口均被占用）。");
            return 1;
        }
        Bootstrap.AfterWebStarted(web);
        Console.WriteLine($"Web 界面：http://127.0.0.1:{web.Port}/（按回车停止）");
        if (ctx.Settings.AutoOpenBrowser)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://127.0.0.1:{web.Port}/")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"自动打开浏览器失败：{ex.Message}");
            }
        }
        // v0.6.6+：正常控制台按回车停止；stdin 重定向（管道/文件）EOF 时退出（修复永久挂起）；
        // 无效 stdin（spawn stdio:ignore，e2e 服务启动方式）Peek 抛异常 → 持续运行直到被外部终止。
        while (true)
        {
            int peek;
            try
            {
                peek = Console.In.Peek();
            }
            catch
            {
                Thread.Sleep(500);
                continue;
            }
            if (peek == -1)
            {
                break;
            }
            Console.ReadLine();
            break;
        }
        WaitForSafeShutdown();
        Bootstrap.Shutdown(web);
        return 0;
    }

    private static void WaitForSafeShutdown()
    {
        DateTime nextNotice = DateTime.MinValue;
        while (!Bootstrap.CanStopServices(out string reason))
        {
            if (DateTime.Now >= nextNotice)
            {
                Logger.Warn($"[退出] 等待任务/编辑会话结束后再停止宿主：{reason}");
                nextNotice = DateTime.Now.AddSeconds(5);
            }
            Thread.Sleep(500);
        }
    }


}
