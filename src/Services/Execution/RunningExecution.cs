using NexusPipeline.Models;

namespace NexusPipeline.Services;

/// <summary>一次脚本或队列运行的可观察状态与并发安全日志/记录快照。</summary>
internal sealed class RunningExecution
{
    private readonly object _stateSync = new();

    private readonly object _logSync = new();

    private readonly List<string> _logLines = new();

    private readonly object _recordsSync = new();

    private string _status = "running";

    private DateTime? _finishedAt;

    private int _doneTasks;

    private string _currentScriptName = "";

    private string _currentStatus = "";

    private int _currentAttempt;

    private int _currentMaxAttempts;

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

    public void IncrementDoneTasks()
    {
        lock (_stateSync)
        {
            _doneTasks++;
        }
    }

    public void AddRecord(RunRecord record)
    {
        lock (_recordsSync)
        {
            Records.Add(record);
        }
    }

    public List<RunRecord> SnapshotRecords()
    {
        lock (_recordsSync)
        {
            return Records.Select(record => record.Clone()).ToList();
        }
    }

    public void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        lock (_logSync)
        {
            _logLines.Add(line);
            if (_logLines.Count > 100)
            {
                _logLines.RemoveRange(0, _logLines.Count - 100);
            }
        }
    }

    public List<string> LogTail(int max = 60)
    {
        lock (_logSync)
        {
            return _logLines.TakeLast(max).ToList();
        }
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
        lock (_stateSync)
        {
            status = _status;
            finishedAt = _finishedAt;
            doneTasks = _doneTasks;
            currentScriptName = _currentScriptName;
            currentStatus = _currentStatus;
            currentAttempt = _currentAttempt;
            currentMaxAttempts = _currentMaxAttempts;
        }

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
            CurrentStatus = currentStatus,
            CurrentAttempt = currentAttempt,
            CurrentMaxAttempts = currentMaxAttempts,
            Records = SnapshotRecords(),
            LogTail = LogTail(60),
        };
    }
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

    public string CurrentStatus { get; init; } = "";

    public int CurrentAttempt { get; init; }

    public int CurrentMaxAttempts { get; init; }

    public IReadOnlyList<RunRecord> Records { get; init; } = Array.Empty<RunRecord>();

    public IReadOnlyList<string> LogTail { get; init; } = Array.Empty<string>();
}
