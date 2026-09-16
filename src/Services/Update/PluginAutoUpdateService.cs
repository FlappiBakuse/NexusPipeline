using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

/// <summary>在插件加载前登记启动更新，并在运行期闲时批量暂存插件更新。</summary>
internal sealed class PluginAutoUpdateService
{
    internal static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    internal static readonly TimeSpan CheckBudget = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DownloadBudget = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan CancellationDrainBudget = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan IdleRetryInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan IdleHorizon = TimeSpan.FromMinutes(5);

    private readonly Func<AppSettings> _settings;
    private readonly IPluginAutoUpdateRepository _repository;
    private readonly Func<TimeSpan, AutoUpdateIdleAttempt> _tryAcquireMaintenance;
    private readonly Func<HostMaintenanceLease, RestartRequestResult> _requestRestart;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly List<PluginPendingIdentity> _startupStagedUpdates = new();
    private readonly List<PluginPendingIdentity> _runtimeStagedUpdates = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset? _nextCheckAt;
    private bool _startupCheckCompleted;
    private bool _runtimeRestartPending;
    private bool _started;

    internal PluginAutoUpdateService(
        Func<AppSettings> settings,
        IPluginAutoUpdateRepository repository,
        Func<TimeSpan, AutoUpdateIdleAttempt> tryAcquireMaintenance,
        Func<HostMaintenanceLease, RestartRequestResult> requestRestart,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _settings = settings;
        _repository = repository;
        _tryAcquireMaintenance = tryAcquireMaintenance;
        _requestRestart = requestRestart;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? DelayScaledAsync;
    }

    /// <summary>宿主更新完成后、插件恢复与 LoadAll 前运行；下载失败不会阻断当前版本启动。</summary>
    internal void RunStartupBeforePlugins()
    {
        if (!_settings().PluginAutoUpdateEnabled)
        {
            return;
        }

        var checkCts = new CancellationTokenSource();
        Task<IReadOnlyList<PluginStoreItem>> check = _repository.GetUpdateCandidatesAsync(checkCts.Token);
        IReadOnlyList<PluginStoreItem> candidates;
        try
        {
            candidates = check.WaitAsync(CheckBudget).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            checkCts.Cancel();
            if (!Drain(check, "启动插件更新检查"))
            {
                ObserveLateCompletion(check, "启动插件更新检查");
            }
            Logger.Warn($"[插件自动更新] 启动检查超过 {CheckBudget.TotalSeconds:0} 秒预算，继续加载当前插件。");
            return;
        }
        catch (Exception ex)
        {
            checkCts.Cancel();
            if (!Drain(check, "启动插件更新检查"))
            {
                ObserveLateCompletion(check, "启动插件更新检查");
            }
            Logger.Warn($"[插件自动更新] 启动检查失败，继续加载当前插件：{ex.Message}");
            return;
        }
        finally
        {
            DisposeAfterCompletion(check, checkCts);
        }

        lock (_sync)
        {
            _startupCheckCompleted = true;
            _nextCheckAt = _now().Add(CheckInterval);
        }
        if (candidates.Count == 0)
        {
            Logger.Info("[插件自动更新] 启动检查完成，没有可升级插件。");
            return;
        }

        Logger.Info($"[插件自动更新] 启动检查发现 {candidates.Count} 个可升级插件，正在服务初始化前下载。");
        var downloadCts = new CancellationTokenSource();
        Task<PluginBatchUpdateResult> download = _repository.StageUpdatesAsync(candidates, downloadCts.Token);
        try
        {
            PluginBatchUpdateResult result = download.WaitAsync(DownloadBudget).GetAwaiter().GetResult();
            RecordStartupStaging(result);
            LogBatchResult("启动", result);
        }
        catch (TimeoutException)
        {
            downloadCts.Cancel();
            bool drained = Drain(download, "启动插件更新下载");
            if (drained && download.Status == TaskStatus.RanToCompletion)
            {
                RecordStartupStaging(download.GetAwaiter().GetResult());
            }
            else if (!drained)
            {
                ObserveLateStartupStaging(download);
            }
            Logger.Warn($"[插件自动更新] 启动下载超过 {DownloadBudget.TotalMinutes:0} 分钟预算，继续加载当前插件。");
        }
        catch (Exception ex)
        {
            downloadCts.Cancel();
            bool drained = Drain(download, "启动插件更新下载");
            if (drained && download.Status == TaskStatus.RanToCompletion)
            {
                RecordStartupStaging(download.GetAwaiter().GetResult());
            }
            else if (!drained)
            {
                ObserveLateStartupStaging(download);
            }
            Logger.Warn($"[插件自动更新] 启动下载失败，继续加载当前插件：{ex.Message}");
        }
        finally
        {
            DisposeAfterCompletion(download, downloadCts);
        }
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_loop is not null)
            {
                return;
            }
            _started = true;
            CancellationTokenSource cts = new();
            CancellationToken token = cts.Token;
            _cts = cts;
            _nextCheckAt = _settings().PluginAutoUpdateEnabled
                ? _now().Add(_startupCheckCompleted ? CheckInterval : InitialCheckDelay)
                : null;
            _loop = Task.Run(() => LoopAsync(token));
        }
        Logger.Info(_settings().PluginAutoUpdateEnabled
            ? $"[插件自动更新] 已安排首次检查：{(_startupCheckCompleted ? CheckInterval : InitialCheckDelay).TotalSeconds:0} 秒"
            : "[插件自动更新] 未启用，等待设置开启");
    }

    internal void OnStartupInstallRecoveryCompleted()
    {
        lock (_sync)
        {
            // 已进入启动应用阶段的同步暂存由 ApplyPending 处理；失败时不自动重启循环重试。
            _startupStagedUpdates.Clear();
        }
    }

    internal void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_sync)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
            _started = false;
        }
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch
        {
        }
        SignalWake();
        try
        {
            loop?.Wait(CancellationDrainBudget);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件自动更新] 停止等待后台任务收尾失败：{ex.GetBaseException().Message}");
        }
        cts.Dispose();
    }

    internal void OnSettingsChanged(AppSettings previous, AppSettings current)
    {
        if (previous.PluginAutoUpdateEnabled != current.PluginAutoUpdateEnabled)
        {
            lock (_sync)
            {
                _nextCheckAt = current.PluginAutoUpdateEnabled
                    ? _now().Add(InitialCheckDelay)
                    : null;
            }
            SignalWake();
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                AppSettings settings = _settings();
                if (!settings.PluginAutoUpdateEnabled)
                {
                    await WaitForWakeAsync(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                    continue;
                }

                RefreshPendingRestartState();
                if (HasRuntimeRestartPending())
                {
                    TryRestartWhenIdle();
                    await WaitForWakeAsync(IdleRetryInterval, token).ConfigureAwait(false);
                    continue;
                }

                DateTimeOffset? nextCheck;
                lock (_sync)
                {
                    _nextCheckAt ??= _now().Add(InitialCheckDelay);
                    nextCheck = _nextCheckAt;
                }
                if (nextCheck is null)
                {
                    continue;
                }
                if (nextCheck.Value <= _now())
                {
                    await CheckAndStageRuntimeUpdatesAsync(token).ConfigureAwait(false);
                    continue;
                }
                await WaitForWakeAsync(nextCheck.Value - _now(), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件自动更新] 协调循环异常：{ex.Message}");
        }
    }

    private async Task CheckAndStageRuntimeUpdatesAsync(CancellationToken token)
    {
        DateTimeOffset checkedAt = _now();
        lock (_sync)
        {
            _nextCheckAt = checkedAt.Add(CheckInterval);
        }

        CancellationTokenSource checkCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<IReadOnlyList<PluginStoreItem>> check = _repository.GetUpdateCandidatesAsync(checkCts.Token);
        IReadOnlyList<PluginStoreItem> candidates;
        try
        {
            candidates = await check.WaitAsync(CheckBudget, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            checkCts.Cancel();
            bool drained = await DrainAsync(check, "插件更新检查").ConfigureAwait(false);
            if (!drained)
            {
                ObserveLateCompletion(check, "插件更新检查");
            }
            Logger.Warn($"[插件自动更新] 定期检查超过 {CheckBudget.TotalSeconds:0} 秒预算。");
            return;
        }
        catch (PluginRepositoryException ex)
        {
            Logger.Warn($"[插件自动更新] 定期检查失败：{ex.Message}");
            return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            checkCts.Cancel();
            if (!await DrainAsync(check, "插件更新检查").ConfigureAwait(false))
            {
                ObserveLateCompletion(check, "插件更新检查");
            }
            return;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件自动更新] 定期检查失败：{ex.Message}");
            return;
        }
        finally
        {
            DisposeAfterCompletion(check, checkCts);
        }

        if (candidates.Count == 0)
        {
            Logger.Info("[插件自动更新] 定期检查完成，没有可升级插件。");
            return;
        }

        Logger.Info($"[插件自动更新] 定期检查发现 {candidates.Count} 个可升级插件，开始下载。");
        CancellationTokenSource downloadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<PluginBatchUpdateResult> download = _repository.StageUpdatesAsync(candidates, downloadCts.Token);
        try
        {
            PluginBatchUpdateResult result = await download.WaitAsync(DownloadBudget, token).ConfigureAwait(false);
            SetRuntimeRestartPending(result.Updated);
            LogBatchResult("定期", result);
        }
        catch (TimeoutException)
        {
            downloadCts.Cancel();
            bool drained = await DrainAsync(download, "插件更新下载").ConfigureAwait(false);
            if (drained && download.Status == TaskStatus.RanToCompletion)
            {
                SetRuntimeRestartPending(download.GetAwaiter().GetResult().Updated);
            }
            else if (!drained)
            {
                ObserveLateRuntimeStaging(download);
            }
            Logger.Warn($"[插件自动更新] 定期下载超过 {DownloadBudget.TotalMinutes:0} 分钟预算。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            downloadCts.Cancel();
            bool drained = await DrainAsync(download, "插件更新下载").ConfigureAwait(false);
            if (drained && download.Status == TaskStatus.RanToCompletion)
            {
                SetRuntimeRestartPending(download.GetAwaiter().GetResult().Updated);
            }
            else if (!drained)
            {
                ObserveLateRuntimeStaging(download);
            }
        }
        catch (Exception ex)
        {
            bool drained = await DrainAsync(download, "插件更新下载").ConfigureAwait(false);
            if (drained && download.Status == TaskStatus.RanToCompletion)
            {
                SetRuntimeRestartPending(download.GetAwaiter().GetResult().Updated);
            }
            else if (!drained)
            {
                ObserveLateRuntimeStaging(download);
            }
            Logger.Warn($"[插件自动更新] 定期下载失败：{ex.Message}");
        }
        finally
        {
            DisposeAfterCompletion(download, downloadCts);
        }
    }

    private void TryRestartWhenIdle()
    {
        if (!RefreshPendingRestartState())
        {
            return;
        }

        AutoUpdateIdleAttempt attempt = _tryAcquireMaintenance(IdleHorizon);
        if (!attempt.Acquired || attempt.Lease is null)
        {
            if (attempt.Blocker is not null)
            {
                Logger.Info($"[插件自动更新] 已暂存更新，等待安全重启：{attempt.Blocker.Message}");
            }
            return;
        }

        if (!RefreshPendingRestartState())
        {
            attempt.Lease.Dispose();
            return;
        }

        RestartRequestResult result = _requestRestart(attempt.Lease);
        if (!result.Accepted)
        {
            Logger.Warn($"[插件自动更新] 已暂存更新，安全重启未受理：{result.Message}");
        }
        else
        {
            Logger.Info("[插件自动更新] 已取得闲时维护租约，正在安全重启以应用插件更新。");
        }
    }

    private bool RefreshPendingRestartState()
    {
        PluginPendingIdentity[] staged;
        lock (_sync)
        {
            if (!_started)
            {
                return false;
            }
            staged = _startupStagedUpdates.Concat(_runtimeStagedUpdates).Distinct().ToArray();
            if (staged.Length == 0)
            {
                _runtimeRestartPending = false;
                return false;
            }
        }

        IReadOnlyList<PluginPendingOperation> pending;
        try
        {
            pending = _repository.ReadPendingOperations();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件自动更新] 无法复核待应用插件更新，暂缓安全重启：{ex.Message}");
            return false;
        }

        PluginPendingIdentity[] stillPending = staged
            .Where(identity => pending.Any(identity.Matches))
            .ToArray();
        lock (_sync)
        {
            _startupStagedUpdates.RemoveAll(identity => !pending.Any(identity.Matches));
            _startupStagedUpdates.RemoveAll(identity => stillPending.Contains(identity));
            _runtimeStagedUpdates.RemoveAll(identity => !pending.Any(identity.Matches));
            foreach (PluginPendingIdentity identity in stillPending)
            {
                if (!_runtimeStagedUpdates.Contains(identity))
                {
                    _runtimeStagedUpdates.Add(identity);
                }
            }
            _runtimeRestartPending = _runtimeStagedUpdates.Count > 0;
            return _runtimeRestartPending;
        }
    }

    private bool HasRuntimeRestartPending()
    {
        lock (_sync)
        {
            return _runtimeRestartPending;
        }
    }

    private void RecordStartupStaging(PluginBatchUpdateResult result)
    {
        PluginPendingIdentity[] stagedUpdates = result.Updated.Select(PluginPendingIdentity.From).ToArray();
        bool serviceStarted;
        lock (_sync)
        {
            foreach (PluginPendingIdentity staged in stagedUpdates)
            {
                if (!_startupStagedUpdates.Contains(staged))
                {
                    _startupStagedUpdates.Add(staged);
                }
            }
            serviceStarted = _started;
        }
        IReadOnlyList<PluginPendingOperation> pending;
        try
        {
            pending = _repository.ReadPendingOperations();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件自动更新] 无法确认启动期 staging 的待应用状态：{ex.Message}");
            return;
        }
        if (serviceStarted)
        {
            lock (_sync)
            {
                foreach (PluginPendingIdentity staged in stagedUpdates.Where(identity => pending.Any(identity.Matches)))
                {
                    if (!_runtimeStagedUpdates.Contains(staged))
                    {
                        _runtimeStagedUpdates.Add(staged);
                    }
                }
                _runtimeRestartPending = _runtimeStagedUpdates.Count > 0;
            }
        }
        SignalWake();
    }

    private void SetRuntimeRestartPending(IReadOnlyList<PluginPendingOperation> updated)
    {
        IReadOnlyList<PluginPendingOperation> pending;
        try
        {
            pending = _repository.ReadPendingOperations();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件自动更新] 无法确认定期 staging 的待应用状态：{ex.Message}");
            return;
        }
        PluginPendingIdentity[] stillPending = updated
            .Select(PluginPendingIdentity.From)
            .Where(identity => pending.Any(identity.Matches))
            .ToArray();
        if (stillPending.Length == 0)
        {
            return;
        }
        lock (_sync)
        {
            foreach (PluginPendingIdentity staged in stillPending)
            {
                if (!_runtimeStagedUpdates.Contains(staged))
                {
                    _runtimeStagedUpdates.Add(staged);
                }
            }
            _runtimeRestartPending = _runtimeStagedUpdates.Count > 0;
        }
        SignalWake();
    }

    private void ObserveLateStartupStaging(Task<PluginBatchUpdateResult> download)
    {
        _ = download.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    PluginBatchUpdateResult result = completed.GetAwaiter().GetResult();
                    RecordStartupStaging(result);
                    if (result.Canceled)
                    {
                        Logger.Info($"[插件自动更新] 启动期取消后保留已暂存的 {result.Updated.Count} 个插件更新。");
                    }
                }
                else if (completed.IsFaulted)
                {
                    Logger.Warn($"[插件自动更新] 启动期晚到下载失败：{completed.Exception?.GetBaseException().Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveLateRuntimeStaging(Task<PluginBatchUpdateResult> download)
    {
        _ = download.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    PluginBatchUpdateResult result = completed.GetAwaiter().GetResult();
                    SetRuntimeRestartPending(result.Updated);
                    if (result.Canceled)
                    {
                        Logger.Info($"[插件自动更新] 定期下载取消后保留已暂存的 {result.Updated.Count} 个插件更新。");
                    }
                }
                else if (completed.IsFaulted)
                {
                    Logger.Warn($"[插件自动更新] 定期晚到下载失败：{completed.Exception?.GetBaseException().Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void LogBatchResult(string phase, PluginBatchUpdateResult result)
    {
        Logger.Info($"[插件自动更新] {phase}下载完成：登记 {result.Updated.Count}/{result.Candidates.Count} 个更新，失败 {result.Failed.Count} 个。");
        foreach (PluginBatchUpdateFailure failure in result.Failed)
        {
            Logger.Warn($"[插件自动更新] {failure.Name} 更新失败（{failure.Code}）：{failure.Message}");
        }
    }

    private static bool Drain(Task operation, string name)
    {
        try
        {
            operation.WaitAsync(CancellationDrainBudget).GetAwaiter().GetResult();
            return true;
        }
        catch (TimeoutException)
        {
            Logger.Warn($"[插件自动更新] {name}取消后仍在收尾。");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[插件自动更新] {name}结束时返回：{ex.GetBaseException().Message}");
            return true;
        }
    }

    private static async Task<bool> DrainAsync(Task operation, string name)
    {
        try
        {
            await operation.WaitAsync(CancellationDrainBudget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            Logger.Warn($"[插件自动更新] {name}取消后仍在收尾。");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[插件自动更新] {name}结束时返回：{ex.GetBaseException().Message}");
            return true;
        }
    }

    private static void ObserveLateCompletion(Task operation, string name)
    {
        _ = operation.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    Logger.Warn($"[插件自动更新] {name}在取消后失败：{completed.Exception?.GetBaseException().Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record PluginPendingIdentity(
        string Name,
        string ArtifactName,
        string Version,
        string StagedPath)
    {
        internal static PluginPendingIdentity From(PluginPendingOperation operation) =>
            new(operation.Name, operation.ArtifactName, operation.Version, operation.StagedPath);

        internal bool Matches(PluginPendingOperation pending) =>
            pending.Action == "update"
            && string.Equals(pending.Name, Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pending.ArtifactName, ArtifactName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pending.Version, Version, StringComparison.Ordinal)
            && string.Equals(pending.StagedPath, StagedPath, StringComparison.OrdinalIgnoreCase);

    }

    private static void DisposeAfterCompletion(Task operation, CancellationTokenSource cancellation)
    {
        if (operation.IsCompleted)
        {
            cancellation.Dispose();
            return;
        }
        _ = operation.ContinueWith(
            _ => cancellation.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForWakeAsync(TimeSpan delay, CancellationToken token)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task timer = _delay(delay, waitCts.Token);
        Task wake = _wake.WaitAsync(waitCts.Token);
        try
        {
            await Task.WhenAny(timer, wake).ConfigureAwait(false);
        }
        finally
        {
            waitCts.Cancel();
        }
    }

    private void SignalWake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static Task DelayScaledAsync(TimeSpan delay, CancellationToken token)
    {
        int milliseconds = Math.Clamp((int)Math.Ceiling(delay.TotalMilliseconds), 1, int.MaxValue);
        return Task.Delay(TestHooks.ScaledMs(milliseconds), token);
    }
}
