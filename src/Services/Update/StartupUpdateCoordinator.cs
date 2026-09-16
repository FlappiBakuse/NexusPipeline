using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

internal enum StartupUpdateDisposition
{
    ContinueStartup,
    RestartForUpdate,
    AbortUnsafeRecovery,
}

/// <summary>在插件扫描、调度和 Web 服务启动前执行一次有界的宿主自动更新。</summary>
internal sealed class StartupUpdateCoordinator
{
    internal static readonly TimeSpan CheckBudget = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DownloadBudget = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan CancellationDrainBudget = TimeSpan.FromSeconds(10);

    private readonly Func<AppSettings> _settings;
    private readonly Func<UpdateState> _getState;
    private readonly Func<UpdateStatusSnapshot> _getStatus;
    private readonly Func<CancellationToken, Task<UpdateStatusSnapshot>> _check;
    private readonly Func<CancellationToken, UpdateDownloadResult> _startDownload;
    private readonly Func<Task> _waitForCurrentOperation;
    private readonly Action _cancelDownload;
    private readonly Func<bool, UpdateApplyResult> _requestApply;
    private readonly Action _recordStartupCheckCompleted;
    private readonly Func<bool> _isStartupRecoveryUnsafe;
    private readonly StartupUpdateAttemptStore _attempts;

    internal StartupUpdateCoordinator(
        Func<AppSettings> settings,
        UpdateService updates,
        UpdateAutomationService automation,
        StartupUpdateAttemptStore? attempts = null)
        : this(
            settings,
            () => updates.State,
            updates.GetStatus,
            token => updates.CheckAsync(Audit.System, token),
            token => updates.StartDownload(Audit.System, token),
            updates.WaitForCurrentOperationAsync,
            () => _ = updates.CancelDownload(),
            defer => updates.RequestApply(defer, Audit.System),
            automation.RecordStartupCheckCompleted,
            attempts,
            () => UpdateApply.StartupRecoveryUnsafe)
    {
    }

    internal StartupUpdateCoordinator(
        Func<AppSettings> settings,
        Func<UpdateState> getState,
        Func<UpdateStatusSnapshot> getStatus,
        Func<CancellationToken, Task<UpdateStatusSnapshot>> check,
        Func<CancellationToken, UpdateDownloadResult> startDownload,
        Func<Task> waitForCurrentOperation,
        Action cancelDownload,
        Func<bool, UpdateApplyResult> requestApply,
        Action recordStartupCheckCompleted,
        StartupUpdateAttemptStore? attempts = null,
        Func<bool>? isStartupRecoveryUnsafe = null)
    {
        _settings = settings;
        _getState = getState;
        _getStatus = getStatus;
        _check = check;
        _startDownload = startDownload;
        _waitForCurrentOperation = waitForCurrentOperation;
        _cancelDownload = cancelDownload;
        _requestApply = requestApply;
        _recordStartupCheckCompleted = recordStartupCheckCompleted;
        _isStartupRecoveryUnsafe = isStartupRecoveryUnsafe ?? (() => UpdateApply.StartupRecoveryUnsafe);
        _attempts = attempts ?? new StartupUpdateAttemptStore();
    }

    internal StartupUpdateDisposition RunBeforeServices()
    {
        AppSettings settings = _settings();
        UpdateState state = _getState();
        if (state == UpdateState.RecoveryPending)
        {
            if (_isStartupRecoveryUnsafe())
            {
                Logger.Error("[更新自动化] 启动恢复未能证明当前宿主文件可安全运行，本次停止服务启动。请先完成更新恢复或处理保留的更新现场。");
                return StartupUpdateDisposition.AbortUnsafeRecovery;
            }

            if (settings.UpdateCheckEnabled && settings.UpdateAutoApplyEnabled)
            {
                _recordStartupCheckCompleted();
            }
            Logger.Warn("[更新自动化] 启动恢复仍有待收尾事务；当前宿主可安全运行，本次跳过自动更新。");
            return StartupUpdateDisposition.ContinueStartup;
        }
        if (state == UpdateState.Ready)
        {
            if (!settings.UpdateCheckEnabled || !settings.UpdateAutoApplyEnabled)
            {
                Logger.Info("[更新自动化] 已有验证完成的更新暂存，自动应用未启用；保留现有用户流程。");
                return StartupUpdateDisposition.ContinueStartup;
            }

            UpdateStatusSnapshot ready = _getStatus();
            if (string.IsNullOrWhiteSpace(ready.Latest) || !ready.CanDownload)
            {
                Logger.Warn("[更新自动化] 已就绪的更新缺少可应用目标，继续启动当前版本。");
                return StartupUpdateDisposition.ContinueStartup;
            }
            if (_attempts.ShouldSuppressAutomaticTarget(ready.Latest))
            {
                Logger.Warn($"[更新自动化] v{ready.Latest} 的自动应用仍在失败冷却期内，继续启动当前版本。");
                return StartupUpdateDisposition.ContinueStartup;
            }
            _recordStartupCheckCompleted();
            try
            {
                _attempts.Mark(ready.Latest);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[更新自动化] 无法写入启动更新恢复标记，保留当前版本继续启动：{ex.Message}");
                return StartupUpdateDisposition.ContinueStartup;
            }

            UpdateApplyResult readyApply = _requestApply(false);
            if (readyApply.Succeeded)
            {
                Logger.Info($"[更新自动化] v{ready.Latest} 已有验证完成的暂存，正在服务初始化前应用。");
                return StartupUpdateDisposition.RestartForUpdate;
            }
            _attempts.Clear();
            Logger.Warn($"[更新自动化] 已就绪的更新暂未能应用，继续启动当前版本：{readyApply.Error}");
            return StartupUpdateDisposition.ContinueStartup;
        }
        if (state != UpdateState.Idle)
        {
            Logger.Error($"[更新自动化] 启动时更新状态为 {state}，无法确认是否可安全继续，本次停止服务启动。");
            return StartupUpdateDisposition.AbortUnsafeRecovery;
        }

        if (!settings.UpdateCheckEnabled || !settings.UpdateAutoApplyEnabled)
        {
            return StartupUpdateDisposition.ContinueStartup;
        }

        UpdateStatusSnapshot status;
        using (var checkCts = new CancellationTokenSource())
        {
            Task<UpdateStatusSnapshot> check = _check(checkCts.Token);
            try
            {
                status = check.WaitAsync(CheckBudget).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                checkCts.Cancel();
                _cancelDownload();
                Drain(check, "更新检查");
                Logger.Warn($"[更新自动化] 启动检查超过 {CheckBudget.TotalSeconds:0} 秒预算，继续启动当前版本。");
                return StartupUpdateDisposition.ContinueStartup;
            }
            catch (Exception ex)
            {
                _cancelDownload();
                Drain(check, "更新检查");
                Logger.Warn($"[更新自动化] 启动检查失败，继续启动当前版本：{ex.Message}");
                return StartupUpdateDisposition.ContinueStartup;
            }
        }

        // 成功完成启动检查后，本次周期已消耗；失败检查仍交给既有运行期自动检查重试。
        _recordStartupCheckCompleted();

        string? previousTarget = _attempts.ReadTargetVersion();
        string currentVersion = UpdateService.CurrentVersion;
        if (previousTarget is not null
            && !status.Available
            && !string.IsNullOrWhiteSpace(status.Error))
        {
            Logger.Warn($"[更新自动化] 本次检查未能连接更新源，保留 v{previousTarget} 的失败冷却标记，后续成功检查再决定是否重试。");
            return StartupUpdateDisposition.ContinueStartup;
        }
        bool retryCooling = !string.IsNullOrWhiteSpace(status.Latest)
            && _attempts.ShouldSuppressAutomaticTarget(status.Latest);
        if (previousTarget is not null
            && string.Equals(previousTarget, status.Latest, StringComparison.OrdinalIgnoreCase)
            && UpdateCatalog.TryParseTag("v" + currentVersion, out NexusVersion current)
            && UpdateCatalog.TryParseTag("v" + previousTarget, out NexusVersion target)
            && UpdateCatalog.Compare(current, target) < 0
            && retryCooling)
        {
            Logger.Warn($"[更新自动化] v{previousTarget} 上次自动更新未完成，仍在 {StartupUpdateAttemptStore.RetryCooldown.TotalHours:0} 小时重试冷却期内；启动当前版本。");
            return StartupUpdateDisposition.ContinueStartup;
        }
        if (previousTarget is not null)
        {
            _attempts.Clear();
        }

        if (!status.Available)
        {
            return StartupUpdateDisposition.ContinueStartup;
        }
        if (!status.CanDownload)
        {
            Logger.Info($"[更新自动化] v{status.Latest} 暂不可自动安装（{status.UpdateBlockCode ?? "policy-unavailable"}），继续启动当前版本。");
            return StartupUpdateDisposition.ContinueStartup;
        }
        if (string.IsNullOrWhiteSpace(status.Latest))
        {
            Logger.Warn("[更新自动化] 启动检查未返回可下载的目标版本，继续启动当前版本。");
            return StartupUpdateDisposition.ContinueStartup;
        }

        try
        {
            // 下载超时或 worker 切换失败后抑制同一 target 的自动重试，防止启动重启循环。
            _attempts.Mark(status.Latest);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新自动化] 无法写入启动更新恢复标记，拒绝自动应用：{ex.Message}");
            return StartupUpdateDisposition.ContinueStartup;
        }

        using var downloadCts = new CancellationTokenSource();
        UpdateDownloadResult started = _startDownload(downloadCts.Token);
        if (!started.Succeeded)
        {
            _attempts.Clear();
            Logger.Warn($"[更新自动化] 启动下载未受理，继续启动当前版本：{started.Error}");
            return StartupUpdateDisposition.ContinueStartup;
        }

        Task download = _waitForCurrentOperation();
        try
        {
            download.WaitAsync(DownloadBudget).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            downloadCts.Cancel();
            _cancelDownload();
            Drain(download, "更新下载");
            Logger.Warn($"[更新自动化] 启动下载超过 {DownloadBudget.TotalMinutes:0} 分钟预算，继续启动当前版本。");
            return StartupUpdateDisposition.ContinueStartup;
        }
        catch (Exception ex)
        {
            _cancelDownload();
            Drain(download, "更新下载");
            Logger.Warn($"[更新自动化] 启动下载失败，继续启动当前版本：{ex.Message}");
            return StartupUpdateDisposition.ContinueStartup;
        }

        status = _getStatus();
        if (status.State != UpdateState.Ready || string.IsNullOrWhiteSpace(status.Latest))
        {
            Logger.Warn($"[更新自动化] 启动下载未生成可应用版本，继续启动当前版本：{status.Error}");
            return StartupUpdateDisposition.ContinueStartup;
        }

        UpdateApplyResult applied = _requestApply(false);
        if (applied.Succeeded)
        {
            Logger.Info($"[更新自动化] v{status.Latest} 已就绪，宿主将在服务初始化前退出并应用更新。");
            return StartupUpdateDisposition.RestartForUpdate;
        }

        _attempts.Clear();
        Logger.Warn($"[更新自动化] 启动时应用未受理，继续启动当前版本：{applied.Error}");
        return StartupUpdateDisposition.ContinueStartup;
    }

    private static void Drain(Task operation, string name)
    {
        try
        {
            operation.WaitAsync(CancellationDrainBudget).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            Logger.Warn($"[更新自动化] {name}取消后仍在收尾；当前版本先继续启动。");
        }
        catch (Exception ex)
        {
            Logger.Debug($"[更新自动化] {name}结束时返回：{ex.Message}");
        }
    }
}
