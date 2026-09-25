using System.Diagnostics;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Execution;

/// <summary>一次脚本或队列运行的可观察状态与并发安全日志/记录快照。</summary>
internal sealed class RunningExecution
{
    internal const int MaxLogEntries = 500;
    internal const int StatusLogEntries = 200;

    private readonly object _stateSync = new();

    private readonly List<ExecutionLogEntry> _logEntries = new();

    private bool _logTruncated;

    private long _nextLogSequence;

    // The current record owns the visible log tail. A queue placeholder uses its
    // frozen item position until a concrete RunRecord is created.
    private string _logSegmentId = "";

    private long _logSegmentSequence;

    private string _logRecordId = "";

    private int _logAttemptNumber;

    private string _status = "running";

    private bool _cancelRequested;

    private string _cancellationPhase = "";

    private long _cancelAcceptedAt;
    private long _cancelSignalAt;
    private long _cancelStopAt;
    private long _cancelExitedAt;
    private long _cancelWorkersAt;
    private long _cancelRestoredAt;
    private long _cancelCommittedAt;

    private DateTime? _finishedAt;

    private int _doneTasks;

    private string _currentScriptName = "";

    private string _currentStatus = "";

    private int _currentAttempt;

    private int _currentMaxAttempts;

    private string _persistenceWarning = "";

    private ExecutionPreviewTarget? _previewTarget;

    private int _previewCaptureInFlight;

    private Action<RunningExecutionStatusSnapshot>? _statusObserver;

    private Action<ExecutionLogEntry>? _logObserver;
    private Action<object>? _taskObserver;
    private readonly Dictionary<string, System.Text.Json.Nodes.JsonObject> _taskReports = new(StringComparer.Ordinal);
    private long _taskRevision;

    internal void UpdateTaskReport(System.Text.Json.Nodes.JsonObject report)
    {
        object change;
        Action<object>? observer;
        lock (_stateSync)
        {
            string recordId = report["runId"]!.GetValue<string>();
            _taskReports[recordId] = (System.Text.Json.Nodes.JsonObject)report.DeepClone();
            change = new { runId = Id, recordId, revision = ++_taskRevision };
            observer = _taskObserver;
        }
        try { observer?.Invoke(change); }
        catch (Exception ex) { Logger.Warn($"[实时事件] 任务观察器失败（{Id}）：{ex.Message}"); }
    }

    internal object SnapshotTasks()
    {
        lock (_stateSync) return new { runId = Id, revision = _taskRevision, reports = _taskReports.Values.Select(r => r.DeepClone()).ToArray() };
    }
    internal IReadOnlyList<System.Text.Json.Nodes.JsonObject> SnapshotTaskReports()
    {
        lock (_stateSync) return _taskReports.Values.Select(r => (System.Text.Json.Nodes.JsonObject)r.DeepClone()).ToArray();
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Kind { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string TargetName { get; set; } = "";

    public string Mode { get; set; } = "";

    public string Status
    {
        get
        {
            lock (_stateSync)
            {
                return _status;
            }
        }
        set
        {
            bool changed;
            lock (_stateSync)
            {
                // An accepted cancellation wins over a not-yet-committed normal end.
                if (_cancelRequested && value == "done") value = "cancelled";
                changed = !string.Equals(_status, value, StringComparison.Ordinal);
                _status = value;
                if (_cancelRequested && value != "running")
                {
                    _cancellationPhase = "terminal";
                    _cancelCommittedAt = Stopwatch.GetTimestamp();
                }
            }
            if (changed)
            {
                NotifyStatusChanged();
            }
        }
    }

    public CancellationRequestResult RequestCancellation()
    {
        lock (_stateSync)
        {
            if (_status != "running" || _finishedAt is not null) return CancellationRequestResult.AlreadyFinished;
            if (_cancelRequested) return CancellationRequestResult.AlreadyRequested;
            _cancelRequested = true;
            _cancelAcceptedAt = Stopwatch.GetTimestamp();
            _cancellationPhase = "stopping";
            _currentStatus = "正在停止任务";
        }
        // Signal before synchronous realtime observers or audit persistence can run.
        try { Cts.Cancel(throwOnFirstException: false); }
        catch (Exception ex) { Logger.Warn($"取消信号发送失败（{TargetName}），任务可能仍在运行：{ex.Message}"); }
        if (Cts.IsCancellationRequested)
        {
            lock (_stateSync) _cancelSignalAt = Stopwatch.GetTimestamp();
        }
        NotifyStatusChanged();
        return CancellationRequestResult.Accepted;
    }

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public DateTime? FinishedAt
    {
        get
        {
            lock (_stateSync)
            {
                return _finishedAt;
            }
        }
        set
        {
            bool changed;
            lock (_stateSync)
            {
                changed = _finishedAt != value;
                _finishedAt = value;
            }
            if (changed)
            {
                NotifyStatusChanged();
            }
        }
    }

    public int TotalTasks { get; set; }

    public int DoneTasks
    {
        get
        {
            lock (_stateSync)
            {
                return _doneTasks;
            }
        }
        set
        {
            bool changed;
            lock (_stateSync)
            {
                changed = _doneTasks != value;
                _doneTasks = value;
            }
            if (changed)
            {
                NotifyStatusChanged();
            }
        }
    }

    public string CurrentScriptName
    {
        get
        {
            lock (_stateSync)
            {
                return _currentScriptName;
            }
        }
        set
        {
            bool changed;
            lock (_stateSync)
            {
                changed = !string.Equals(_currentScriptName, value, StringComparison.Ordinal);
                _currentScriptName = value;
            }
            if (changed)
            {
                NotifyStatusChanged();
            }
        }
    }

    public string CurrentScriptId
    {
        get
        {
            lock (_stateSync)
            {
                return _previewTarget?.ScriptId ?? "";
            }
        }
    }

    public string CurrentStatus
    {
        get
        {
            lock (_stateSync)
            {
                return _currentStatus;
            }
        }
        set
        {
            bool changed;
            lock (_stateSync)
            {
                if (_cancelRequested && _status == "running" && value is not ("正在停止脚本" or "脚本已停止，正在收拢后台任务" or "后台任务已收拢" or "正在恢复配置" or "配置恢复完成" or "配置恢复失败，现场已保留")) return;
                changed = !string.Equals(_currentStatus, value, StringComparison.Ordinal);
                _currentStatus = value;
                if (_cancelRequested && value == "正在停止脚本") _cancellationPhase = "stopping";
                if (_cancelRequested && value == "正在停止脚本") _cancelStopAt = Stopwatch.GetTimestamp();
                if (_cancelRequested && value == "脚本已停止，正在收拢后台任务") { _cancellationPhase = "quiescing"; _cancelExitedAt = Stopwatch.GetTimestamp(); }
                if (_cancelRequested && value == "后台任务已收拢") _cancelWorkersAt = Stopwatch.GetTimestamp();
                if (_cancelRequested && value == "正在恢复配置") _cancellationPhase = "restoring";
                if (_cancelRequested && value is "配置恢复完成" or "配置恢复失败，现场已保留") _cancelRestoredAt = Stopwatch.GetTimestamp();
            }
            if (changed)
            {
                NotifyStatusChanged();
            }
        }
    }

    public int CurrentAttempt
    {
        get
        {
            lock (_stateSync)
            {
                return _currentAttempt;
            }
        }
        set
        {
            bool changed;
            lock (_stateSync)
            {
                changed = _currentAttempt != value;
                _currentAttempt = value;
            }
            if (changed)
            {
                NotifyStatusChanged();
            }
        }
    }

    public int CurrentMaxAttempts
    {
        get
        {
            lock (_stateSync)
            {
                return _currentMaxAttempts;
            }
        }
        set
        {
            bool changed;
            lock (_stateSync)
            {
                changed = _currentMaxAttempts != value;
                _currentMaxAttempts = value;
            }
            if (changed)
            {
                NotifyStatusChanged();
            }
        }
    }

    public List<RunRecord> Records { get; set; } = new();

    public CancellationTokenSource Cts { get; set; } = new();

    public Task Completion { get; set; } = Task.CompletedTask;

    public string PersistenceWarning
    {
        get
        {
            lock (_stateSync)
            {
                return _persistenceWarning;
            }
        }
    }

    public bool LogTruncated
    {
        get
        {
            lock (_stateSync)
            {
                return _logTruncated;
            }
        }
    }

    public string LogSegmentId
    {
        get
        {
            lock (_stateSync) return _logSegmentId;
        }
    }

    public bool BeginLogSegment(string segmentId)
        => BeginLogSegment(segmentId, null, null, null);

    public bool BeginLogSegment(string segmentId, int? attempt, int? maxAttempts, string? recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        lock (_stateSync)
        {
            if (_cancelRequested || _status != "running") return false;
            if (string.Equals(_logSegmentId, segmentId, StringComparison.Ordinal)) return true;
            _logSegmentId = segmentId;
            _logSegmentSequence++;
            _logRecordId = recordId ?? "";
            _logAttemptNumber = attempt ?? 0;
            _logEntries.Clear();
            _logTruncated = false;
            if (attempt is { } number)
            {
                _currentAttempt = number;
                _currentMaxAttempts = maxAttempts ?? _currentMaxAttempts;
                _currentStatus = "正在准备当前尝试";
            }
        }
        NotifyStatusChanged();
        return true;
    }

    public void SetPersistenceWarning(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }
        bool changed;
        lock (_stateSync)
        {
            string previous = _persistenceWarning;
            _persistenceWarning = string.IsNullOrWhiteSpace(_persistenceWarning)
                ? warning
                : $"{_persistenceWarning}；{warning}";
            changed = !string.Equals(previous, _persistenceWarning, StringComparison.Ordinal);
        }
        if (changed)
        {
            NotifyStatusChanged();
        }
    }

    public void IncrementDoneTasks()
    {
        lock (_stateSync)
        {
            _doneTasks++;
        }
        NotifyStatusChanged();
    }

    public void AddRecord(RunRecord record)
    {
        lock (_stateSync)
        {
            Records.Add(record);
        }
    }

    public void AddRecordAndIncrement(RunRecord record)
    {
        lock (_stateSync)
        {
            Records.Add(record);
            _doneTasks++;
        }
        NotifyStatusChanged();
    }

    public List<RunRecord> SnapshotRecords()
    {
        lock (_stateSync)
        {
            return Records.Select(record => record.Clone()).ToList();
        }
    }

    public void AppendLog(string line)
    {
        AppendLog(LogLevel.Info, line);
    }

    public void AppendLog(LogLevel level, string line, string? segmentId = null)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        ExecutionLogEntry entry;
        lock (_stateSync)
        {
            // Output callbacks from a completed record cannot enter a later user's view.
            if (segmentId is not null && !string.Equals(segmentId, _logSegmentId, StringComparison.Ordinal)) return;
            DateTimeOffset timestamp = DateTimeOffset.Now;
            entry = new ExecutionLogEntry(
                ++_nextLogSequence,
                timestamp,
                level,
                line,
                Logger.FormatLine(level, line, timestamp)) { LogSegmentId = _logSegmentId };
            _logEntries.Add(entry);
            if (_logEntries.Count > MaxLogEntries)
            {
                _logEntries.RemoveRange(0, _logEntries.Count - MaxLogEntries);
                _logTruncated = true;
            }
        }
        NotifyLogAppended(entry);
    }

    public List<string> LogTail(int max = 60)
    {
        lock (_stateSync)
        {
            return _logEntries.TakeLast(max).Select(entry => entry.FormattedText).ToList();
        }
    }

    public List<ExecutionLogEntry> LogEntries(int max = StatusLogEntries)
    {
        lock (_stateSync)
        {
            return _logEntries.TakeLast(Math.Max(0, max)).ToList();
        }
    }

    internal void SetPreviewTarget(ExecutionPreviewTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_stateSync)
        {
            _previewTarget = target;
        }
        NotifyStatusChanged();
    }

    internal void SetPreviewWaiting(ScriptInstance script)
    {
        bool configured = !string.IsNullOrWhiteSpace(script.GameExe);
        SetPreviewTarget(new ExecutionPreviewTarget(
            script.Id,
            script.Name,
            configured
                ? (script.GameMode == "emulator" ? ExecutionPreviewSource.Emulator : ExecutionPreviewSource.Pc)
                : ExecutionPreviewSource.None,
            configured ? ExecutionPreviewState.Waiting : ExecutionPreviewState.Unavailable,
            Error: configured ? null : "未配置游戏目标"));
    }

    internal void ClearPreviewTarget()
    {
        bool changed;
        lock (_stateSync)
        {
            changed = _previewTarget is not null;
            _previewTarget = null;
        }
        if (changed)
        {
            NotifyStatusChanged();
        }
    }

    internal void AttachRealtimeObservers(
        Action<RunningExecutionStatusSnapshot> statusObserver,
        Action<ExecutionLogEntry> logObserver,
        Action<object>? taskObserver = null)
    {
        ArgumentNullException.ThrowIfNull(statusObserver);
        ArgumentNullException.ThrowIfNull(logObserver);
        lock (_stateSync)
        {
            _statusObserver = statusObserver;
            _logObserver = logObserver;
            _taskObserver = taskObserver;
        }
    }

    internal RunningExecutionStatusSnapshot SnapshotStatus()
    {
        lock (_stateSync)
        {
            return CreateStatusSnapshotLocked();
        }
    }

    private RunningExecutionStatusSnapshot CreateStatusSnapshotLocked()
    {
        return new RunningExecutionStatusSnapshot
        {
            Id = Id,
            Kind = Kind,
            TargetId = TargetId,
            TargetName = TargetName,
            Mode = Mode,
            Status = _status,
            StartedAt = StartedAt,
            FinishedAt = _finishedAt,
            TotalTasks = TotalTasks,
            DoneTasks = _doneTasks,
            CurrentScriptName = _currentScriptName,
            CurrentScriptId = _previewTarget?.ScriptId ?? "",
            CurrentStatus = _currentStatus,
            CurrentAttempt = _currentAttempt,
            CurrentMaxAttempts = _currentMaxAttempts,
            PersistenceWarning = _persistenceWarning,
            LogTruncated = _logTruncated,
            LogSegmentId = _logSegmentId,
            LogSegmentSequence = _logSegmentSequence,
            LogSegment = CreateLogSegmentLocked(),
            CancelRequested = _cancelRequested,
            CancellationPhase = _cancellationPhase,
            CancellationTimingMs = CreateCancellationTimingLocked(),
        };
    }

    private void NotifyStatusChanged()
    {
        Action<RunningExecutionStatusSnapshot>? observer;
        RunningExecutionStatusSnapshot snapshot;
        lock (_stateSync)
        {
            observer = _statusObserver;
            if (observer is null)
            {
                return;
            }
            snapshot = CreateStatusSnapshotLocked();
        }
        try
        {
            observer(snapshot);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[实时事件] 运行状态观察器失败（{Id}）：{ex.Message}");
        }
    }

    private void NotifyLogAppended(ExecutionLogEntry entry)
    {
        Action<ExecutionLogEntry>? observer;
        lock (_stateSync)
        {
            observer = _logObserver;
        }
        if (observer is null)
        {
            return;
        }
        try
        {
            observer(entry);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[实时事件] 运行日志观察器失败（{Id}）：{ex.Message}");
        }
    }

    internal ExecutionPreviewTarget? PreviewTarget
    {
        get
        {
            lock (_stateSync)
            {
                return _previewTarget;
            }
        }
    }

    internal bool TryBeginPreviewCapture()
    {
        return Interlocked.CompareExchange(ref _previewCaptureInFlight, 1, 0) == 0;
    }

    internal void EndPreviewCapture()
    {
        Volatile.Write(ref _previewCaptureInFlight, 0);
    }

    /// <summary>读取一致的运行标量、记录和日志尾部，供 Web/CLI 观察线程使用。</summary>
    public RunningExecutionSnapshot Snapshot()
    {
        string status;
        DateTime? finishedAt;
        int doneTasks;
        string currentScriptName;
        string currentStatus;
        int currentAttempt;
        int currentMaxAttempts;
        string persistenceWarning;
        ExecutionPreviewTarget? previewTarget;
        lock (_stateSync)
        {
            status = _status;
            finishedAt = _finishedAt;
            doneTasks = _doneTasks;
            currentScriptName = _currentScriptName;
            currentStatus = _currentStatus;
            currentAttempt = _currentAttempt;
            currentMaxAttempts = _currentMaxAttempts;
            persistenceWarning = _persistenceWarning;
            previewTarget = _previewTarget;

            return new RunningExecutionSnapshot
            {
                Id = Id,
                Kind = Kind,
                TargetId = TargetId,
                TargetName = TargetName,
                Mode = Mode,
                Status = status,
                StartedAt = StartedAt,
                FinishedAt = finishedAt,
                TotalTasks = TotalTasks,
                DoneTasks = doneTasks,
                CurrentScriptName = currentScriptName,
                CurrentScriptId = previewTarget?.ScriptId ?? "",
                CurrentStatus = currentStatus,
                CurrentAttempt = currentAttempt,
                CurrentMaxAttempts = currentMaxAttempts,
                PersistenceWarning = persistenceWarning,
                LogTruncated = _logTruncated,
                LogSegmentId = _logSegmentId,
                LogSegmentSequence = _logSegmentSequence,
                LogSegment = CreateLogSegmentLocked(),
                CancelRequested = _cancelRequested,
                CancellationPhase = _cancellationPhase,
                CancellationTimingMs = CreateCancellationTimingLocked(),
                Records = Records.Select(record => record.Clone()).ToList(),
                LogTail = _logEntries.TakeLast(60).Select(entry => entry.FormattedText).ToList(),
                LogEntries = _logEntries.TakeLast(StatusLogEntries).ToList(),
            };
        }
    }

    private CancellationTimingSnapshot? CreateCancellationTimingLocked()
    {
        if (_cancelAcceptedAt == 0) return null;
        double? Elapsed(long timestamp) => timestamp == 0 ? null : Math.Round(Stopwatch.GetElapsedTime(_cancelAcceptedAt, timestamp).TotalMilliseconds, 3);
        return new CancellationTimingSnapshot(
            Elapsed(_cancelSignalAt), Elapsed(_cancelStopAt), Elapsed(_cancelExitedAt),
            Elapsed(_cancelWorkersAt), Elapsed(_cancelRestoredAt), Elapsed(_cancelCommittedAt));
    }

    private LogSegmentProjection? CreateLogSegmentLocked() => _logSegmentId.Length == 0 ? null
        : new LogSegmentProjection(_logSegmentId, _logSegmentSequence,
            _logRecordId.Length == 0 ? null : _logRecordId, _logAttemptNumber);
}

internal sealed record LogSegmentProjection(string Id, long Generation, string? RunRecordId, int AttemptNumber);

internal sealed record CancellationTimingSnapshot(
    double? SignalSent,
    double? StopIssued,
    double? OwnedProcessesExited,
    double? WorkersQuiesced,
    double? RestoreFinished,
    double? ResultCommitted);

internal sealed record ExecutionLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    string FormattedText)
{
    public string LogSegmentId { get; init; } = "";
}

internal enum CancellationRequestResult
{
    Accepted,
    AlreadyRequested,
    AlreadyFinished,
}

internal sealed record RunningExecutionStatusSnapshot
{
    public string Id { get; init; } = "";

    public string Kind { get; init; } = "";

    public string TargetId { get; init; } = "";

    public string TargetName { get; init; } = "";

    public string Mode { get; init; } = "";

    public string Status { get; init; } = "";

    public DateTime StartedAt { get; init; }

    public DateTime? FinishedAt { get; init; }

    public int TotalTasks { get; init; }

    public int DoneTasks { get; init; }

    public string CurrentScriptName { get; init; } = "";

    public string CurrentScriptId { get; init; } = "";

    public string CurrentStatus { get; init; } = "";

    public int CurrentAttempt { get; init; }

    public int CurrentMaxAttempts { get; init; }

    public string PersistenceWarning { get; init; } = "";

    public bool LogTruncated { get; init; }

    public string LogSegmentId { get; init; } = "";

    public long LogSegmentSequence { get; init; }

    public LogSegmentProjection? LogSegment { get; init; }

    public bool CancelRequested { get; init; }

    public string CancellationPhase { get; init; } = "";

    public CancellationTimingSnapshot? CancellationTimingMs { get; init; }
}

internal sealed record RunningExecutionSnapshot
{
    public string Id { get; init; } = "";

    public string Kind { get; init; } = "";

    public string TargetId { get; init; } = "";

    public string TargetName { get; init; } = "";

    public string Mode { get; init; } = "";

    public string Status { get; init; } = "";

    public DateTime StartedAt { get; init; }

    public DateTime? FinishedAt { get; init; }

    public int TotalTasks { get; init; }

    public int DoneTasks { get; init; }

    public string CurrentScriptName { get; init; } = "";

    public string CurrentScriptId { get; init; } = "";

    public string CurrentStatus { get; init; } = "";

    public int CurrentAttempt { get; init; }

    public int CurrentMaxAttempts { get; init; }

    public string PersistenceWarning { get; init; } = "";

    public bool LogTruncated { get; init; }

    public string LogSegmentId { get; init; } = "";

    public long LogSegmentSequence { get; init; }

    public LogSegmentProjection? LogSegment { get; init; }

    public bool CancelRequested { get; init; }

    public string CancellationPhase { get; init; } = "";

    public CancellationTimingSnapshot? CancellationTimingMs { get; init; }

    public IReadOnlyList<RunRecord> Records { get; init; } = Array.Empty<RunRecord>();

    public IReadOnlyList<string> LogTail { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ExecutionLogEntry> LogEntries { get; init; } = Array.Empty<ExecutionLogEntry>();
}
