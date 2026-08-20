using NexusPipeline.Models;

namespace NexusPipeline.Services;

/// <summary>一次脚本或队列运行的可观察状态与并发安全日志/记录快照。</summary>
internal sealed class RunningExecution
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Kind { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string TargetName { get; set; } = "";

    public string Mode { get; set; } = "";

    public string Status { get; set; } = "running";

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public DateTime? FinishedAt { get; set; }

    public int TotalTasks { get; set; }

    public int DoneTasks { get; set; }

    public string CurrentScriptName { get; set; } = "";

    public string CurrentStatus { get; set; } = "";

    public int CurrentAttempt { get; set; }

    public int CurrentMaxAttempts { get; set; }

    public List<RunRecord> Records { get; set; } = new();

    public CancellationTokenSource Cts { get; set; } = new();

    public Task Completion { get; set; } = Task.CompletedTask;

    private readonly object _logSync = new();

    private readonly List<string> _logLines = new();

    /// <summary>运行记录读写锁（v0.7.2+，KN-04）：运行后台线程追加记录与 Web 请求线程序列化并发时保护集合。</summary>
    private readonly object _recordsSync = new();

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
            return Records.ToList();
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
}
