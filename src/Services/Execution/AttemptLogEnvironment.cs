using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 一次尝试的日志路径环境（结构拆分，自 ExecutionCoordinator.RunAttemptCoreAsync 抽出）：
/// 尝试起点候选快照（fresh/续读判定）、轮换/替换/截断的监控生命周期。行为与拆分前一致，零外部 API 变化。
/// </summary>
internal sealed class AttemptLogEnvironment
{
    private readonly ScriptInstance _script;
    private readonly string _modeText;
    private readonly Dictionary<string, LogCandidateSnapshot> _snapshotsAtAttemptStart;

    public AttemptLogEnvironment(ScriptInstance script, string modeText)
    {
        _script = script;
        _modeText = modeText;
        _snapshotsAtAttemptStart = CaptureLogCandidates(script.LogPath);
    }

    /// <summary>创建初始日志监控：文件在尝试开始前不存在（本次新建）→ 从头读；已存在（含残留日志）→ 从「尝试开始时长度」续读。</summary>
    public LogMonitor? CreateMonitor(string? resolvedBeforeStart)
    {
        if (resolvedBeforeStart is null)
        {
            return null;
        }
        return NewMonitor(resolvedBeforeStart, SnapshotForCandidate(resolvedBeforeStart), rotated: false);
    }

    /// <summary>监控循环每轮刷新：路径轮换（换路径）重开监控、同路径文件被替换（FileId）重开从头读；无变化返回原监控。</summary>
    public LogMonitor? RefreshMonitor(LogMonitor? monitor)
    {
        if (string.IsNullOrWhiteSpace(_script.LogPath))
        {
            return monitor;
        }
        string? resolved = LogPattern.ResolveFile(_script.LogPath);
        if (resolved is null)
        {
            return monitor;
        }
        if (monitor is null)
        {
            return NewMonitor(resolved, SnapshotForCandidate(resolved), rotated: false);
        }
        if (!string.Equals(resolved, monitor.Path, StringComparison.OrdinalIgnoreCase))
        {
            monitor.Dispose();
            return NewMonitor(resolved, SnapshotForCandidate(resolved), rotated: true);
        }
        try
        {
            if (monitor.FileReplaced(resolved))
            {
                monitor.ReopenFromStart();
                Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」日志文件被替换，已重新从头读取：{resolved}");
            }
        }
        catch (Exception)
        {
        }
        return monitor;
    }

    private LogMonitor NewMonitor(string resolved, LogCandidateSnapshot? beforeStart, bool rotated)
    {
        LogCandidateSnapshot? current = LogMonitor.CaptureSnapshot(resolved);
        (bool fresh, long initialPosition) = LogMonitor.DecideStart(beforeStart, current);
        var monitor = new LogMonitor(resolved, readFromStart: fresh, initialPosition: initialPosition);
        Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」{(rotated ? "日志轮换，改监控" : "开始监控")}：{resolved}（{(fresh ? "从头" : "续读")}）");
        return monitor;
    }

    private LogCandidateSnapshot? SnapshotForCandidate(string path)
    {
        return _snapshotsAtAttemptStart.TryGetValue(SnapshotKey(path), out LogCandidateSnapshot? snapshot)
            ? snapshot
            : null;
    }

    /// <summary>Attempt 起点一次性记录日志格式下所有候选的 path/FileId/length；后续通配符轮换按这张快照决定读取起点。</summary>
    private static Dictionary<string, LogCandidateSnapshot> CaptureLogCandidates(string? pattern)
    {
        var snapshots = new Dictionary<string, LogCandidateSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return snapshots;
        }
        foreach (string candidate in LogPattern.ResolveFiles(pattern))
        {
            LogCandidateSnapshot? snapshot = LogMonitor.CaptureSnapshot(candidate);
            if (snapshot is not null)
            {
                snapshots[SnapshotKey(candidate)] = snapshot;
            }
        }
        return snapshots;
    }

    private static string SnapshotKey(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}