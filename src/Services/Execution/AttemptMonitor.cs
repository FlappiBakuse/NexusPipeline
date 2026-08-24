using System.Diagnostics;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

internal readonly record struct StallObservation(bool Hit, string Reason);

/// <summary>
/// 一次 Attempt 的轻量监控边界：日志增量、进程退出和 stall 观察都在这里完成，
/// Judge/config sync 只通过 worker 交互，避免把 IO 执行塞回判定循环。
/// </summary>
internal sealed class AttemptMonitor
{
    public string ReadLog(LogMonitor? monitor)
    {
        return monitor?.ReadNew() ?? "";
    }

    public bool IsScriptExited(
        Process? rootProcess,
        string launchExe,
        ProcessOwnership? ownership,
        string? excludeGame)
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
        return rootExited && !SystemActions.IsExeRunning(launchExe) && !ownedAlive;
    }

    public StallObservation CheckStall(
        LogMonitor? monitor,
        bool logConfigured,
        DateTime attemptStart,
        DateTime? firstEntryAt,
        int stallTimeoutMinutes)
    {
        if (stallTimeoutMinutes <= 0)
        {
            return new StallObservation(false, "");
        }
        double stallSeconds = TestHooks.ScaledSeconds(stallTimeoutMinutes * 60);
        if (monitor is null && logConfigured)
        {
            double waitSeconds = (DateTime.Now - attemptStart).TotalSeconds;
            return waitSeconds >= stallSeconds
                ? new StallObservation(true, $"启动后 {stallTimeoutMinutes} 分钟未产生日志条目（未找到日志文件）")
                : new StallObservation(false, "");
        }
        if (monitor is not null && firstEntryAt is null)
        {
            double waitSeconds = (DateTime.Now - attemptStart).TotalSeconds;
            return waitSeconds >= stallSeconds
                ? new StallObservation(true, $"启动后 {stallTimeoutMinutes} 分钟未产生日志条目")
                : new StallObservation(false, "");
        }
        if (monitor is not null)
        {
            double stallSecondsSinceWrite = (DateTime.Now - monitor.LastWrite).TotalSeconds;
            return stallSecondsSinceWrite >= stallSeconds
                ? new StallObservation(true, $"日志超过 {stallTimeoutMinutes} 分钟无更新")
                : new StallObservation(false, "");
        }
        return new StallObservation(false, "");
    }
}
