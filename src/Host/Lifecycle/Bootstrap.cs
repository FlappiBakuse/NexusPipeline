using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.ControlPlane.Mcp;
using NexusPipeline.Host.Composition;
using NexusPipeline.Host;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Testing;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Localization;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Configuration.Recovery;

namespace NexusPipeline.Host.Lifecycle;

/// <summary>服务启动/停止编排：插件、历史清理、调度器、配置恢复、更新自动化与 Web 服务。</summary>
internal sealed class Bootstrap
{
    internal const string ActiveRunsReason = "active_runs";
    internal const string ConfigEditSessionsReason = "config_edit_sessions";
    internal const string PendingSystemActionReason = "pending_system_action";

    private readonly object _restartSync = new();
    private readonly HostRuntime _runtime;
    private readonly PluginAutoUpdateService _pluginAutoUpdateService;
    private readonly UpdateAutomationService _updates;
    private readonly Func<bool> _requestServiceExit;
    private readonly Func<bool> _requestWebOnlyExit;
    private HostRestartCoordinator? _restartCoordinator;
    private bool _startupRecoveryReady;
    private bool _startupRecoveryFailed;
    private int _servicesStarted;
    private int _shutdownCompleted;

    internal Bootstrap(
        HostRuntime runtime,
        PluginAutoUpdateService pluginAutoUpdateService,
        UpdateAutomationService updates,
        Func<bool> requestServiceExit,
        Func<bool> requestWebOnlyExit)
    {
        _runtime = runtime;
        _pluginAutoUpdateService = pluginAutoUpdateService;
        _updates = updates;
        _requestServiceExit = requestServiceExit;
        _requestWebOnlyExit = requestWebOnlyExit;
    }

    /// <summary>加载插件、清理过期历史、启动调度器、配置恢复重试与更新自动化。</summary>
    public void StartServices()
    {
        if (Volatile.Read(ref _shutdownCompleted) != 0)
        {
            throw new InvalidOperationException("Host services cannot start after shutdown.");
        }

        if (Interlocked.CompareExchange(ref _servicesStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException("Host services have already started.");
        }
        try
        {
        bool pendingApplied;
        if (_startupRecoveryReady)
        {
            pendingApplied = true;
            _startupRecoveryReady = false;
        }
        else if (_startupRecoveryFailed)
        {
            pendingApplied = false;
        }
        else
        {
            pendingApplied = PluginInstallRecovery.ApplyPending();
        }
        if (!pendingApplied)
        {
            Logger.Warn("[插件] 存在未完成的插件安装事务，保留 pending 并继续加载当前插件。");
        }
        if (pendingApplied)
        {
            _pluginAutoUpdateService.OnStartupInstallRecoveryCompleted();
        }
        AppearanceLegacyMigration.ApplyOnce();
        _runtime.Plugins.LoadAll();
        _runtime.History.Cleanup(_runtime.Settings.HistoryRetentionDays);
        _runtime.Scheduler.Start();
        ConfigRecoveryService.StartRecoveryRetry();
        _updates.Start();
        if (pendingApplied)
        {
            _pluginAutoUpdateService.Start();
        }
        }
        catch
        {
            Volatile.Write(ref _servicesStarted, 0);
            throw;
        }
    }

    /// <summary>在 Bootstrap 加载插件前运行启动期插件更新检查与暂存。</summary>
    internal bool PrepareStartupPluginUpdates()
    {
        // 现有 journal 必须先在当前进程中完成；只有成功后，RepositoryService
        // 才能按启动时冻结的 channel 查询 catalog 并创建新的暂存事务。
        bool pendingApplied = PluginInstallRecovery.ApplyPending();
        _startupRecoveryReady = pendingApplied;
        _startupRecoveryFailed = !pendingApplied;
        if (!pendingApplied)
        {
            Logger.Warn("[插件] 启动恢复失败，禁止本次启动创建新的插件仓库事务。");
            return false;
        }
        _pluginAutoUpdateService.RunStartupBeforePlugins();
        return true;
    }

    internal void OnSettingsChanged(AppSettings previous, AppSettings current)
    {
        _pluginAutoUpdateService.OnSettingsChanged(previous, current);
    }

    /// <summary>启动 Web 服务：端口被占用自动 +1 重试（最多 20 次）。每次重试新建实例（HttpListener 或托管 loopback transport 启动失败后不可复用）；非端口冲突异常直接返回 null（不崩溃）。失败返回 null。</summary>
    public WebServer? StartWebWithRetry(int basePort, WebServerOptions? options = null)
    {
        int port = basePort;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var web = new WebServer(_runtime.HttpRoutes);
                web.Start(port, options);
                return web;
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
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

    /// <summary>Web 服务启动成功后的收尾：远程访问模式确保实际监听端口的 TCP 入站规则存在。</summary>
    public void AfterWebStarted(WebServer web)
    {
        if (web.AllowsRemoteAccess)
        {
            FirewallRule.EnsureAllowInbound(web.Port);
        }
    }

    internal bool CanStopServices(out string reasonCode)
    {
        if (_runtime.Center.Active.Count > 0)
        {
            reasonCode = ActiveRunsReason;
            return false;
        }
        if (ConfigEditSessionRegistry.EditSessions.Count > 0)
        {
            reasonCode = ConfigEditSessionsReason;
            return false;
        }
        reasonCode = "";
        return true;
    }

    internal bool CanRequestDirectExit(out string reasonCode)
    {
        if (!CanStopServices(out reasonCode))
        {
            return false;
        }
        if (_runtime.Center.CurrentSystemAction is not null)
        {
            reasonCode = PendingSystemActionReason;
            return false;
        }
        return true;
    }

    internal static string LocalizeExitReason(string reasonCode, string? locale = null)
    {
        string normalized = locale ?? LocaleCatalog.HostLocale;
        return reasonCode switch
        {
            ActiveRunsReason => HostLocalization.TranslateNamed(
                "exit.active_runs",
                "There are active runs. Wait for them to finish or cancel them first.",
                locale: normalized),
            ConfigEditSessionsReason => HostLocalization.TranslateNamed(
                "exit.config_edit_sessions",
                "There are configuration edit sessions. Finish or cancel them first.",
                locale: normalized),
            PendingSystemActionReason => HostLocalization.TranslateNamed(
                "exit.pending_system_action",
                "There is a pending system action. Complete or cancel it first.",
                locale: normalized),
            _ => HostLocalization.TranslateNamed(
                "exit.unknown_reason",
                "NexusPipeline cannot exit right now. Try again later.",
                locale: normalized),
        };
    }

    internal static string LocalizeExitLog(string key, string fallback, string reasonCode, string? locale = null)
    {
        string normalized = locale ?? LocaleCatalog.HostLocale;
        return HostLocalization.TranslateNamed(
            key,
            fallback,
            new Dictionary<string, object?>
            {
                ["reason"] = LocalizeExitReason(reasonCode, normalized),
            },
            normalized);
    }

    /// <summary>按设置启动内嵌 MCP；MCP 端口冲突不自动漂移，失败不影响 Control API。</summary>
    public McpHost? StartMcp()
    {
        if (!_runtime.Settings.McpEnabled)
        {
            return null;
        }
        var mcp = new McpHost(
            _runtime.CreateMcpToolContext(() => TryRequestRestart(Audit.Mcp)));
        return mcp.TryStart(_runtime.Settings.McpPort) ? mcp : null;
    }

    internal (HostMaintenanceLease? Lease, string? Reason) TryAcquireUpdateMaintenanceLease()
    {
        HostMaintenanceLease? lease = _runtime.Center.TryAcquireMaintenanceLease(out string reason);
        return (lease, lease is null ? reason : null);
    }

    internal bool TryRequestDirectExit()
    {
        if (CanRequestDirectExit(out string reasonCode))
        {
            System.Windows.Forms.Application.Exit();
            return true;
        }
        string reason = LocalizeExitReason(reasonCode);
        Logger.Warn(LocalizeExitLog(
            "exit.request_rejected",
            "Exit request rejected: {reason}",
            reasonCode));
        try
        {
            System.Windows.Forms.MessageBox.Show(
                reason,
                HostLocalization.TranslateNamed(
                    "exit.blocked_title",
                    "NexusPipeline cannot exit",
                    locale: LocaleCatalog.HostLocale),
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }
        catch
        {
        }
        return false;
    }

    internal bool TryRequestCompletionExit()
    {
        if (!CanStopServices(out string reasonCode))
        {
            Logger.Warn(LocalizeExitLog(
                "exit.completion_delayed",
                "Completion exit request delayed: {reason}",
                reasonCode));
            return false;
        }
        System.Windows.Forms.Application.Exit();
        return true;
    }

    /// <summary>统一的服务重启入口，供 Web、CLI 间接调用和 MCP 共享原子维护租约。</summary>
    internal bool TryRequestRestart(string auditSource)
    {
        return RequestRestart(auditSource).Accepted;
    }

    internal RestartRequestResult RequestRestart(string auditSource)
    {
        if (ApplicationHost.IsWebOnly)
        {
            return RestartRequestResult.Failure(
                "operation_forbidden",
                "当前为仅网页模式（web），不支持自动重启，请手动重启");
        }
        int newPort = _runtime.Settings.WebPort;
        HostRestartCoordinator coordinator = GetRestartCoordinator();
        RestartRequestResult result = coordinator.Request(auditSource, newPort);
        if (!result.Accepted)
        {
            Logger.Warn($"[重启] 已拒绝重启请求：{result.Message}");
        }
        return result;
    }

    /// <summary>将自动更新闲时策略已取得的维护租约转交给统一重启流程。</summary>
    internal RestartRequestResult RequestRestartWithLease(
        HostMaintenanceLease lease,
        string auditSource)
    {
        int newPort = _runtime.Settings.WebPort;
        HostRestartCoordinator coordinator = GetRestartCoordinator();
        RestartRequestResult result = coordinator.RequestWithLease(auditSource, newPort, lease);
        if (!result.Accepted)
        {
            Logger.Warn($"[重启] 已拒绝自动更新重启请求：{result.Message}");
        }
        return result;
    }

    private HostRestartCoordinator GetRestartCoordinator()
    {
        lock (_restartSync)
        {
            _restartCoordinator ??= new HostRestartCoordinator(
                acquireMaintenance: () =>
                {
                    HostMaintenanceLease? lease = _runtime.Center.TryAcquireMaintenanceLease(out string reason);
                    return (lease, lease is null ? reason : null);
                },
                launchChild: LaunchRestartChild,
                requestExit: () => ApplicationHost.IsWebOnly
                    ? _requestWebOnlyExit()
                    : RequestRestartExit(),
                delay: duration => Thread.Sleep(TestHooks.ScaledMs((int)Math.Max(1, duration.TotalMilliseconds))),
                launchDelay: TimeSpan.FromSeconds(1));
            return _restartCoordinator;
        }
    }

    internal static string[] BuildRestartArguments(string handoffId, bool webOnly) =>
        ApplicationHost.BuildRestartArguments(handoffId, webOnly);

    private bool LaunchRestartChild(string handoffId)
    {
        string exePath = Environment.ProcessPath ?? "";
        if (string.IsNullOrWhiteSpace(exePath))
        {
            Logger.Error("[重启] 无法获取当前程序路径，放弃重启。");
            return false;
        }
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // 交接标识及运行模式一同传给子进程；web-only 安全重启需回到原有界面入口。
        foreach (string argument in BuildRestartArguments(handoffId, ApplicationHost.IsWebOnly))
        {
            startInfo.ArgumentList.Add(argument);
        }
        Process? child = Process.Start(startInfo);
        return child is not null;
    }

    private bool RequestRestartExit()
    {
        return _requestServiceExit();
    }

    /// <summary>
    /// 更新应用后的宿主退出：常驻服务走完成操作退出门禁；
    /// web 模式没有 WinForms 消息循环（Application.Exit 无效），直接延时退出进程——单实例互斥体随进程终止释放，
    /// apply-update 子进程接管切换。
    /// </summary>
    internal bool TryRequestUpdateExit()
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
    public void Shutdown(WebServer? web)
    {
        Shutdown(web, null);
    }

    public void Shutdown(WebServer? web, McpHost? mcp, bool force = false)
    {
        if (!force && !CanStopServices(out string reasonCode))
        {
            Logger.Warn(LocalizeExitLog(
                "exit.shutdown_blocked",
                "The host shutdown was refused because services are still active: {reason}",
                reasonCode));
            return;
        }
        if (Interlocked.Exchange(ref _shutdownCompleted, 1) != 0)
        {
            return;
        }
        Volatile.Write(ref _servicesStarted, 0);

        // Stop producers and new admissions first. The normal caller has already
        // waited for active runs/edit sessions through CanStopServices; force is
        // reserved for a failed startup/Dispose path where no public work may leak.
        try
        {
            _pluginAutoUpdateService.Stop();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 插件自动更新停止异常：{ex.Message}");
        }
        try
        {
            _updates.Stop();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 更新自动化停止异常：{ex.Message}");
        }
        try
        {
            _runtime.Scheduler.Stop();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 调度器停止异常：{ex.Message}");
        }
        try
        {
            ConfigRecoveryService.StopRecoveryRetry();
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
            mcp?.Stop();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] MCP 服务停止异常：{ex.Message}");
        }
        try
        {
            _runtime.Plugins.ShutdownAll();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 插件停止异常：{ex.Message}");
        }
    }
}
