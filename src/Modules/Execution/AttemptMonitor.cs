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

    public AttemptMonitor(bool isMxu = false) => _isMxu = isMxu;

    public void MarkInput() => Interlocked.Exchange(ref _lastInputStamp, Stopwatch.GetTimestamp());

    public void ObserveConsoleLine(string line)
    {
        MarkInput();
        ObserveLogLine(line);
    }

    public void ObserveLogLine(string line)
    {
        // This is a reported upstream startup failure, not an inferred lack of
        // progress. Both console callbacks and file monitors are attempt-scoped.
        if (line.Contains("任务启动失败：未搜索到任何窗口", StringComparison.Ordinal)
            || line.Contains("任务启动失败: 未搜索到任何窗口", StringComparison.Ordinal))
            Interlocked.CompareExchange(ref _startupFailure,
                new("上游任务启动失败：未搜索到任何窗口", "run.startup_window_missing"), null);
        if (_isMxu && (line.Contains("[MXU_LAUNCH] Failed to spawn program:", StringComparison.Ordinal)
            || line.Contains("[MXU_LAUNCH] Failed to run program:", StringComparison.Ordinal)
            || line.Contains("[MXU_LAUNCH] Failed to parse param JSON:", StringComparison.Ordinal)
            || line.Contains("[MXU_LAUNCH] Missing or empty 'program' parameter", StringComparison.Ordinal)))
            Interlocked.CompareExchange(ref _startupFailure,
                new("上游 MXU 启动动作失败，请检查当前实例的启动配置和日志", "run.upstream_launch_failed"), null);
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
        AttemptProcessSnapshot? processSnapshot)
    {
        bool ownedAlive = ownership?.Snapshot().Any(identity =>
            excludeGame is null
            || rootProcess is not null && identity.Pid == rootProcess.Id
            || !string.Equals(Path.GetFileNameWithoutExtension(identity.ImageName), excludeGame, StringComparison.OrdinalIgnoreCase)) == true;
        bool rootExited;
        try
        {
            rootExited = rootProcess is null || rootProcess.HasExited;
        }
        catch (InvalidOperationException)
        {
            rootExited = true;
        }
        bool launchRunning = processSnapshot?.IsExecutableRunning(launchExe) ?? SystemActions.IsExeRunning(launchExe);
        return rootExited && !launchRunning && !ownedAlive;
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
