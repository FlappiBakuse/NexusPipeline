using NexusPipeline.Cli;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;
using NexusPipeline.Mcp;
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
        if (!PrepareHostedStart())
        {
            return;
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.ReloadSettings();
        ctx.ReloadData();
        // 崩溃恢复仅常驻服务执行（manage/web/CLI 由运行时自愈 RecoverIfNeeded 兜底），避免多进程并发恢复竞争文件。
        UserConfigManager.RecoverInterrupted(ctx.SnapshotUsers());
        TaskRegistration.SyncWithSettings(ctx.Settings);
        Bootstrap.StartServices();

        WebServerOptions webOptions = WebServerOptions.FromSettings(
            ctx.Settings.LightweightMode,
            ctx.Settings.AllowRemoteAccess);
        WebServer? web = Bootstrap.StartWebWithRetry(ctx.Settings.WebPort, webOptions);
        if (web is not null)
        {
            Bootstrap.AfterWebStarted(web);
            if (webOptions.ServeWebUi && ctx.Settings.AutoOpenBrowser)
            {
                TrayApp.OpenWeb(web.Port);
            }
        }
        McpHost? mcp = web is null ? null : Bootstrap.StartMcp();
        if (!webOptions.ServeWebUi)
        {
            Logger.Info("轻量运行模式：Control API 已启动并仅绑定 127.0.0.1，不提供 Web UI 与浏览器。");
        }
        if (web is null)
        {
            Logger.Error("[错误] Control API 启动失败，服务无法提供控制面。");
            Bootstrap.Shutdown(null, mcp);
            ClearServicePid();
            return;
        }

        // 启动时按设置自动检查一次更新（仅检查不下载）。
        ScheduleStartupUpdateCheck();

#if NEXUS_TEST_HOST
        StartTestHostExitMonitor();
#endif
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());

        ShutdownHosted(web, mcp, "NexusPipeline 已退出。");
    }

    /// <summary>
    /// 创建单实例互斥体并取得所有权：处理「服务被强杀后互斥体被遗弃」——构造函数会抛
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

    /// <summary>自动重启分支：等待旧进程释放单实例互斥体（旧进程收到退出指令后 ~1 秒退出并释放，
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
        // web 模式同样抢单实例互斥——常驻服务已在运行时直接退出（防两实例双写配置/数据）。
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
        // web 模式同样执行更新事务启动收尾（defer 时退出由本模式专用退出端口处理）。
        if (!PrepareHostedStart())
        {
            return 0;
        }
        ApplicationHost.IsWebOnly = true;
        RuntimeContext ctx = RuntimeContext.Instance;
        // 崩溃恢复仅服务类进程执行（service/web 均含调度与配置交换能力；manage/status/CLI 由运行时自愈兜底）。
        UserConfigManager.RecoverInterrupted(ctx.SnapshotUsers());
        Bootstrap.StartServices();
        WebServer? web = Bootstrap.StartWebWithRetry(
            ctx.Settings.WebPort,
            new WebServerOptions(ServeWebUi: !ctx.Settings.LightweightMode, AllowRemoteAccess: ctx.Settings.AllowRemoteAccess));
        if (web is null)
        {
            ClearServicePid();
            Console.WriteLine("[错误] 无法启动 Web 服务（端口均被占用）。");
            return 1;
        }
        Bootstrap.AfterWebStarted(web);
        McpHost? mcp = Bootstrap.StartMcp();
        ScheduleStartupUpdateCheck();
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
#if NEXUS_TEST_HOST
        string? testHostExitFile = TestHostExitFilePath();
        if (testHostExitFile is not null)
        {
            while (!File.Exists(testHostExitFile))
            {
                Thread.Sleep(100);
            }
        }
        else
#endif
        {
            // 正常控制台按回车停止；stdin 重定向（管道/文件）EOF 时退出（修复永久挂起）；
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
        }
        ShutdownHosted(web, mcp);
        return 0;
    }

    /// <summary>
    /// 常驻模式（service/web）共享的启动不变量：准备当前运行时目录 → 更新事务启动收尾 → 写 service.pid。
    /// 返回 false 表示更新收尾已拉起 apply-update 子进程或完成回滚，本进程应立即退出。
    /// </summary>
    private static bool PrepareHostedStart()
    {
        AppPaths.RuntimeState.EnsureDirectories();
        if (UpdateApply.RunStartupFinalization())
        {
            return false;
        }
        WriteServicePid();
        return true;
    }

    /// <summary>常驻模式（service/web）共享的关闭不变量：等待任务/编辑会话安全结束 → 停服务 → 清 service.pid。</summary>
    private static void ShutdownHosted(WebServer? web, McpHost? mcp, string? exitLog = null)
    {
        WaitForSafeShutdown();
        Bootstrap.Shutdown(web, mcp);
        ClearServicePid();
        if (exitLog is not null)
        {
            Logger.Info(exitLog);
        }
    }

    private static void WriteServicePid()
    {
        try
        {
            File.WriteAllText(AppPaths.ServicePidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[运行时] 写入 service.pid 失败：{ex.Message}");
        }
    }

    private static void ClearServicePid()
    {
        try
        {
            if (File.Exists(AppPaths.ServicePidPath))
            {
                File.Delete(AppPaths.ServicePidPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[运行时] 清理 service.pid 失败：{ex.Message}");
        }
    }

    /// <summary>启动时按设置自动检查一次更新（仅检查不下载，；复用状态机互斥，失败仅告警）。</summary>
    private static void ScheduleStartupUpdateCheck()
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (!ctx.Settings.UpdateCheckEnabled)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TestHooks.ScaledMs(5000)).ConfigureAwait(false);
                UpdateService service = ctx.Resolve<UpdateService>();
                await service.CheckAsync(Audit.System).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[更新] 启动检查失败：{ex.Message}");
            }
        });
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

#if NEXUS_TEST_HOST
    private static string? TestHostExitFilePath()
    {
        string? value = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXIT_FILE")?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void StartTestHostExitMonitor()
    {
        string? exitFile = TestHostExitFilePath();
        if (exitFile is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                if (File.Exists(exitFile))
                {
                    try
                    {
                        Application.Exit();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Test Host 退出信号处理失败：{ex.Message}");
                    }
                    return;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
        });
    }
#endif


}
