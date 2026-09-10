using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

/// <summary>
/// 更新自动化协调器：编排定期检查、自动下载、闲时等待和自动应用；更新事务本身仍由 UpdateService 负责。
/// </summary>
internal sealed class UpdateAutomationService
{
    internal static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(12);
    internal static readonly TimeSpan IdleRetryInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan IdleHorizon = TimeSpan.FromMinutes(5);

    private readonly Func<AppSettings> _settings;
    private readonly Func<UpdateStatusSnapshot> _getStatus;
    private readonly Func<string, Task<UpdateStatusSnapshot>> _check;
    private readonly Func<string, string?> _startDownload;
    private readonly Func<HostMaintenanceLease, string, UpdateApplyResult> _applyWithLease;
    private readonly Func<TimeSpan, AutoUpdateIdleAttempt> _tryAcquireIdle;
    private readonly Action _invalidateDiscovery;
    private readonly Func<bool> _isAutomaticApplyAllowed;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _initialCheckDelay;
    private readonly TimeSpan _automaticCheckInterval;
    private readonly TimeSpan _idleRetryInterval;
    private readonly TimeSpan _idleHorizon;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset? _lastAutomaticCheckAt;
    private DateTimeOffset? _nextAutomaticCheckAt;
    private long _checkScheduleRevision;
    private bool _waitingForIdle;
    private AutoUpdateIdleBlocker? _idleBlocker;
    private string? _lastLoggedBlocker;

    internal UpdateAutomationService(
        Func<AppSettings> settings,
        UpdateService updates,
        AutoUpdateIdlePolicy idlePolicy,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? initialCheckDelay = null,
        TimeSpan? automaticCheckInterval = null,
        TimeSpan? idleRetryInterval = null,
        TimeSpan? idleHorizon = null)
        : this(
            settings,
            updates.GetStatus,
            updates.CheckAsync,
            updates.StartDownload,
            updates.RequestImmediateApplyWithLease,
            idlePolicy.TryAcquire,
            updates.InvalidateDiscovery,
            () => updates.IsAutomaticApplyAllowed,
            now,
            delay,
            initialCheckDelay,
            automaticCheckInterval,
            idleRetryInterval,
            idleHorizon)
    {
    }

    internal UpdateAutomationService(
        Func<AppSettings> settings,
        Func<UpdateStatusSnapshot> getStatus,
        Func<string, Task<UpdateStatusSnapshot>> check,
        Func<string, string?> startDownload,
        Func<HostMaintenanceLease, string, UpdateApplyResult> applyWithLease,
        Func<TimeSpan, AutoUpdateIdleAttempt> tryAcquireIdle,
        Action invalidateDiscovery,
        Func<bool>? isAutomaticApplyAllowed = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? initialCheckDelay = null,
        TimeSpan? automaticCheckInterval = null,
        TimeSpan? idleRetryInterval = null,
        TimeSpan? idleHorizon = null)
    {
        _settings = settings;
        _getStatus = getStatus;
        _check = check;
        _startDownload = startDownload;
        _applyWithLease = applyWithLease;
        _tryAcquireIdle = tryAcquireIdle;
        _invalidateDiscovery = invalidateDiscovery;
        _isAutomaticApplyAllowed = isAutomaticApplyAllowed ?? (() => true);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? DelayScaledAsync;
        _initialCheckDelay = initialCheckDelay ?? InitialCheckDelay;
        _automaticCheckInterval = automaticCheckInterval ?? AutomaticCheckInterval;
        _idleRetryInterval = idleRetryInterval ?? IdleRetryInterval;
        _idleHorizon = idleHorizon ?? IdleHorizon;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_loop is not null)
            {
                return;
            }
            _cts = new CancellationTokenSource();
            _nextAutomaticCheckAt = _settings().UpdateCheckEnabled
                ? _now().Add(_initialCheckDelay)
                : null;
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        if (_settings().UpdateCheckEnabled)
        {
            Logger.Info($"[更新自动化] 已安排首次检查：{_initialCheckDelay.TotalSeconds:0} 秒");
        }
        else
        {
            Logger.Info("[更新自动化] 定期检查未启用，等待设置开启");
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            _loop = null;
            _waitingForIdle = false;
            _idleBlocker = null;
            _lastLoggedBlocker = null;
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
        // 后台检查由其取消令牌收尾，停止流程不等待外部网络请求返回。
    }

    public void OnSettingsChanged(AppSettings previous, AppSettings current)
    {
        bool sourceChanged = !string.Equals(previous.UpdateChannel, current.UpdateChannel, StringComparison.Ordinal)
            || !string.Equals(previous.UpdateSourceUrl, current.UpdateSourceUrl, StringComparison.Ordinal);
        if (sourceChanged)
        {
            _invalidateDiscovery();
        }

        lock (_sync)
        {
            if (sourceChanged || previous.UpdateCheckEnabled != current.UpdateCheckEnabled)
            {
                _checkScheduleRevision++;
            }
            if (!current.UpdateCheckEnabled)
            {
                _nextAutomaticCheckAt = null;
                _waitingForIdle = false;
                _idleBlocker = null;
                _lastLoggedBlocker = null;
            }
            else if (!previous.UpdateCheckEnabled || sourceChanged)
            {
                _nextAutomaticCheckAt = _now().Add(_initialCheckDelay);
            }
            if (!current.UpdateAutoApplyEnabled)
            {
                _waitingForIdle = false;
                _idleBlocker = null;
                _lastLoggedBlocker = null;
            }
        }
        SignalWake();
    }

    public UpdateAutomationSnapshot GetSnapshot()
    {
        AppSettings settings = _settings();
        lock (_sync)
        {
            bool checkEnabled = settings.UpdateCheckEnabled;
            bool autoUpdateEnabled = checkEnabled && settings.UpdateAutoApplyEnabled;
            return new UpdateAutomationSnapshot(
                checkEnabled,
                autoUpdateEnabled,
                _lastAutomaticCheckAt,
                checkEnabled ? _nextAutomaticCheckAt : null,
                autoUpdateEnabled && _waitingForIdle,
                autoUpdateEnabled ? _idleBlocker?.Code : null,
                autoUpdateEnabled ? _idleBlocker?.Message : null);
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    AppSettings settings = _settings();
                    if (!settings.UpdateCheckEnabled)
                    {
                        ClearIdleWait();
                        await WaitForWakeAsync(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                        continue;
                    }

                    EnsureNextCheckScheduled();
                    if (await TryRunAutomaticCheckAsync(settings, token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    UpdateStatusSnapshot status = _getStatus();
                    if (settings.UpdateAutoApplyEnabled
                        && status.State == UpdateState.Ready
                        && _isAutomaticApplyAllowed())
                    {
                        TryApplyWhenIdle();
                        await WaitForWakeAsync(_idleRetryInterval, token).ConfigureAwait(false);
                        continue;
                    }

                    ClearIdleWait();
                    TimeSpan wait = NextWait(status);
                    await WaitForWakeAsync(wait, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[更新自动化] 协调循环异常：{ex.Message}");
                    await WaitForWakeAsync(_idleRetryInterval, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private bool EnsureNextCheckScheduled()
    {
        lock (_sync)
        {
            if (_nextAutomaticCheckAt is not null)
            {
                return false;
            }
            _nextAutomaticCheckAt = _now().Add(_initialCheckDelay);
            return true;
        }
    }

    private async Task<bool> TryRunAutomaticCheckAsync(AppSettings settings, CancellationToken token)
    {
        DateTimeOffset now = _now();
        long scheduleRevision;
        lock (_sync)
        {
            if (_nextAutomaticCheckAt is null || _nextAutomaticCheckAt > now)
            {
                return false;
            }
            scheduleRevision = _checkScheduleRevision;
        }

        Logger.Info("[更新自动化] 开始定期检查");
        UpdateStatusSnapshot status = _getStatus();
        bool scheduleChanged = false;
        try
        {
            status = await _check(Audit.System).ConfigureAwait(false);
        }
        finally
        {
            DateTimeOffset completedAt = _now();
            lock (_sync)
            {
                _lastAutomaticCheckAt = completedAt;
                scheduleChanged = scheduleRevision != _checkScheduleRevision;
                _nextAutomaticCheckAt = _settings().UpdateCheckEnabled
                    ? completedAt.Add(scheduleChanged ? _initialCheckDelay : _automaticCheckInterval)
                    : null;
            }
        }

        Logger.Info($"[更新自动化] 定期检查完成（状态={status.State}，可用更新={status.Available}）");
        if (token.IsCancellationRequested || scheduleChanged)
        {
            return true;
        }

        AppSettings current = _settings();
        if (current.UpdateCheckEnabled && current.UpdateAutoApplyEnabled
            && status.State == UpdateState.Idle && status.Available)
        {
            string? error = _startDownload(Audit.System);
            if (error is null)
            {
                Logger.Info($"[更新自动化] 开始自动下载 v{status.Latest}");
            }
            else
            {
                Logger.Warn($"[更新自动化] 自动下载未启动：{error}");
            }
        }
        return true;
    }

    private void TryApplyWhenIdle()
    {
        AutoUpdateIdleAttempt attempt = _tryAcquireIdle(_idleHorizon);
        if (!attempt.Acquired || attempt.Lease is null)
        {
            SetIdleBlocker(attempt.Blocker ?? new AutoUpdateIdleBlocker(
                "host_busy",
                "宿主当前繁忙，等待下一次闲时检查",
                null,
                null));
            return;
        }

        ClearIdleWait();
        UpdateApplyResult result = _applyWithLease(attempt.Lease, Audit.System);
        if (result.Succeeded)
        {
            Logger.Info("[更新自动化] 宿主已空闲，开始自动应用更新");
            return;
        }

        SetIdleBlocker(new AutoUpdateIdleBlocker(
            "apply_rejected",
            result.Error ?? "自动应用暂未受理，等待下一次闲时检查",
            null,
            null));
    }

    private TimeSpan NextWait(UpdateStatusSnapshot status)
    {
        if (status.State is UpdateState.Checking or UpdateState.Downloading)
        {
            return TimeSpan.FromSeconds(1);
        }

        DateTimeOffset? next;
        lock (_sync)
        {
            next = _nextAutomaticCheckAt;
        }
        if (next is null)
        {
            return TimeSpan.FromMinutes(1);
        }
        return next.Value <= _now() ? TimeSpan.Zero : next.Value - _now();
    }

    private void SetIdleBlocker(AutoUpdateIdleBlocker blocker)
    {
        string key = $"{blocker.Code}|{blocker.QueueName}|{blocker.TriggerTime:O}|{blocker.Message}";
        bool log = false;
        lock (_sync)
        {
            _waitingForIdle = true;
            _idleBlocker = blocker;
            if (!string.Equals(_lastLoggedBlocker, key, StringComparison.Ordinal))
            {
                _lastLoggedBlocker = key;
                log = true;
            }
        }
        if (log)
        {
            Logger.Info($"[更新自动化] 暂不能应用：{blocker.Message}");
        }
    }

    private void ClearIdleWait()
    {
        lock (_sync)
        {
            _waitingForIdle = false;
            _idleBlocker = null;
            _lastLoggedBlocker = null;
        }
    }

    private async Task WaitForWakeAsync(TimeSpan delay, CancellationToken token)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }
        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
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
        double milliseconds = Math.Max(1, delay.TotalMilliseconds);
        int scaled = milliseconds >= int.MaxValue
            ? int.MaxValue
            : TestHooks.ScaledMs((int)Math.Ceiling(milliseconds));
        return Task.Delay(scaled, token);
    }
}

/// <summary>更新自动化维度快照；时间字段不写入设置文件，进程重启后重新安排首次检查。</summary>
internal sealed record UpdateAutomationSnapshot(
    bool CheckEnabled,
    bool AutoUpdateEnabled,
    DateTimeOffset? LastAutomaticCheckAt,
    DateTimeOffset? NextAutomaticCheckAt,
    bool WaitingForIdle,
    string? IdleBlockCode,
    string? IdleBlockReason);
