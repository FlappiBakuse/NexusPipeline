using System.Diagnostics;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Testing;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Modules.Execution;

internal readonly record struct StallObservation(bool Hit, string Reason);
internal sealed record UpstreamStartupFailure(string Detail, string Code);

/// <summary>
/// 一次 Attempt 的轻量监控边界：日志增量、进程退出和 stall 观察都在这里完成，
/// Judge/config sync 只通过 worker 交互，避免把 IO 执行塞回判定循环。
/// </summary>
internal sealed class AttemptMonitor
{
    private readonly long _startedStamp = Stopwatch.GetTimestamp();
    private long _lastInputStamp;

    private readonly bool _isMxu;
    private UpstreamStartupFailure? _startupFailure;
    private bool _observedOwnedAutomation;

    public AttemptMonitor(bool isMxu = false) => _isMxu = isMxu;

    public void MarkInput() => Interlocked.Exchange(ref _lastInputStamp, Stopwatch.GetTimestamp());

    public void ObserveConsoleLine(string line)
    {
        MarkInput();
        ObserveLogLine(line);
    }

    public void ObserveLogLine(string line)
    {
        // This diagnostic belongs to the MXU startup phase. A generic log may
        // quote these words while reporting a recovered error or past attempt.
        // Require the entire upstream message, not a substring in arbitrary logs.
        if (_isMxu && (line.Trim() is "任务启动失败：未搜索到任何窗口"
            or "任务启动失败: 未搜索到任何窗口"))
            Interlocked.CompareExchange(ref _startupFailure,
                new("上游任务启动失败：未搜索到任何窗口", "run.startup_window_missing"), null);
        if (_isMxu && IsMxuLaunchFailure(line))
            Interlocked.CompareExchange(ref _startupFailure,
                new("上游 MXU 启动动作失败，请检查当前实例的启动配置和日志", "run.upstream_launch_failed"), null);
    }

    private static bool IsMxuLaunchFailure(string line)
    {
        string message = line.Trim();
        const string tag = "[MXU_LAUNCH] ";
        int tagAt = message.IndexOf(tag, StringComparison.Ordinal);
        if (tagAt < 0) return false;
        // Upstream file logs may prefix an error with a timestamp and level.
        // Do not accept an arbitrary sentence or a quoted historical entry.
        string prefix = message[..tagAt];
        if (prefix.Length > 0
            && (!prefix.Contains(" ERROR ", StringComparison.Ordinal)
                || prefix.Length < 12
                || !char.IsDigit(prefix[0])))
            return false;
        string suffix = message[(tagAt + tag.Length)..];
        return suffix.StartsWith("Failed to spawn program:", StringComparison.Ordinal)
            || suffix.StartsWith("Failed to run program:", StringComparison.Ordinal)
            || suffix.StartsWith("Failed to parse param JSON:", StringComparison.Ordinal)
            || suffix.StartsWith("Missing or empty 'program' parameter", StringComparison.Ordinal);
    }

    public UpstreamStartupFailure? StartupFailure => Volatile.Read(ref _startupFailure);

    public string ReadLog(LogMonitor? monitor)
    {
        return monitor?.ReadNew() ?? "";
    }

    public bool IsScriptExited(
        Process? rootProcess,
        string launchExe,
        ProcessOwnership? ownership,
        string? excludeGame,
        AttemptProcessSnapshot? processSnapshot,
        ProcessIdentity? preservedLauncher = null,
        bool automationBoundaryConfirmed = false,
        bool knownRequiredWorkersStopped = true)
    {
        ProcessObservation? observation = ownership?.Observe();
        // Missing Job evidence is not evidence that a detached writer or worker exited.
        bool ownedAlive = observation is { IsComplete: false }
            || (preservedLauncher is not null && (ownership?.HasAssignedProcess != true || observation is null))
            || observation?.Identities.Any(identity =>
                (preservedLauncher is null || !preservedLauncher.Value.Matches(identity))
                && (preservedLauncher is null || !ProcessRoleClassifier.IsConsoleSidecar(identity))
                && (
                excludeGame is null
                || rootProcess is not null && identity.Pid == rootProcess.Id
                || !ProcessTree.IsSameProcessName(identity.ImageName, excludeGame))) == true;
        if (preservedLauncher is not null && observation is { IsComplete: true } && ownedAlive)
            _observedOwnedAutomation = true;
        bool rootExited;
        try
        {
            rootExited = rootProcess is null || rootProcess.HasExited;
        }
        catch (InvalidOperationException)
        {
            rootExited = false;
        }
        bool launchRunning = preservedLauncher is null
            && (processSnapshot?.IsExecutableRunning(launchExe) ?? SystemActions.IsExeRunning(launchExe));
        // A launcher may create the worker after the first empty Job sample.
        // Its pure role alone is not an automation completion boundary.
        bool retainedBoundary = preservedLauncher is not null
            && (automationBoundaryConfirmed || _observedOwnedAutomation);
        return knownRequiredWorkersStopped && (rootExited || retainedBoundary) && !launchRunning && !ownedAlive;
    }

    public StallObservation CheckStall(
        LogMonitor? monitor,
        bool logConfigured,
        int stallTimeoutMinutes)
    {
        if (stallTimeoutMinutes <= 0)
        {
            return new StallObservation(false, "");
        }
        double stallSeconds = TestHooks.ScaledSeconds(stallTimeoutMinutes * 60);
        long lastInput = Interlocked.Read(ref _lastInputStamp);
        double idleSeconds = Stopwatch.GetElapsedTime(lastInput == 0 ? _startedStamp : lastInput).TotalSeconds;
        if (idleSeconds < stallSeconds) return new StallObservation(false, "");
        string reason = lastInput == 0
            ? monitor is null && logConfigured
                ? $"启动后 {stallTimeoutMinutes} 分钟未产生日志条目（未找到日志文件，stdout/stderr 也无输出）"
                : $"启动后 {stallTimeoutMinutes} 分钟未产生日志条目"
            : $"日志超过 {stallTimeoutMinutes} 分钟无新增输入";
        return new StallObservation(true, reason);
    }
}
