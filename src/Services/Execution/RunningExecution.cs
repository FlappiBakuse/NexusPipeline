using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>一次脚本或队列运行的可观察状态与并发安全日志/记录快照。</summary>
internal sealed class RunningExecution
{
    internal const int MaxLogEntries = 500;
    internal const int StatusLogEntries = 200;

    private readonly object _stateSync = new();

    private readonly List<ExecutionLogEntry> _logEntries = new();

    private long _nextLogSequence;

    private string _status = "running";

    private DateTime? _finishedAt;

    private int _doneTasks;

    private string _currentScriptName = "";

    private string _currentStatus = "";

    private int _currentAttempt;

    private int _currentMaxAttempts;

    private string _persistenceWarning = "";

    private ExecutionPreviewTarget? _previewTarget;

    private int _previewCaptureInFlight;

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
            lock (_stateSync)
            {
                _status = value;
            }
        }
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
            lock (_stateSync)
            {
                _finishedAt = value;
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
            lock (_stateSync)
            {
                _doneTasks = value;
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
            lock (_stateSync)
            {
                _currentScriptName = value;
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
            lock (_stateSync)
            {
                _currentStatus = value;
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
            lock (_stateSync)
            {
                _currentAttempt = value;
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
            lock (_stateSync)
            {
                _currentMaxAttempts = value;
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

    public void SetPersistenceWarning(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }
        lock (_stateSync)
        {
            _persistenceWarning = string.IsNullOrWhiteSpace(_persistenceWarning)
                ? warning
                : $"{_persistenceWarning}；{warning}";
        }
    }

    public void IncrementDoneTasks()
    {
        lock (_stateSync)
        {
            _doneTasks++;
        }
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

    public void AppendLog(LogLevel level, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        lock (_stateSync)
        {
            DateTimeOffset timestamp = DateTimeOffset.Now;
            _logEntries.Add(new ExecutionLogEntry(
                ++_nextLogSequence,
                timestamp,
                level,
                line,
                Logger.FormatLine(level, line, timestamp)));
            if (_logEntries.Count > MaxLogEntries)
            {
                _logEntries.RemoveRange(0, _logEntries.Count - MaxLogEntries);
            }
        }
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
        lock (_stateSync)
        {
            _previewTarget = null;
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
                Records = Records.Select(record => record.Clone()).ToList(),
                LogTail = _logEntries.TakeLast(60).Select(entry => entry.FormattedText).ToList(),
                LogEntries = _logEntries.TakeLast(StatusLogEntries).ToList(),
            };
        }
    }
}

internal sealed record ExecutionLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    string FormattedText);

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

    public IReadOnlyList<RunRecord> Records { get; init; } = Array.Empty<RunRecord>();

    public IReadOnlyList<string> LogTail { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ExecutionLogEntry> LogEntries { get; init; } = Array.Empty<ExecutionLogEntry>();
}
