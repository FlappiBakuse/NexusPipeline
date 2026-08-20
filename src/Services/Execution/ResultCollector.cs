using System.Text;

namespace NexusPipeline.Services.Execution;

/// <summary>运行日志与按尝试日志段收集器，避免执行协调器同时管理日志容量和落盘分段。</summary>
internal sealed class ResultCollector
{
    private const int MaxScriptLogBytes = 20 * 1024 * 1024;

    private readonly StringBuilder _fullLog = new();
    private readonly List<string> _attemptSegments = new();
    private bool _truncated;
    private int _attemptStart;

    public StringBuilder FullLog => _fullLog;

    public List<string> AttemptSegments => _attemptSegments;

    public int AttemptStart
    {
        get => _attemptStart;
        set => _attemptStart = value;
    }

    public bool IsTruncated
    {
        get => _truncated;
        set => _truncated = value;
    }

    public void Append(string line)
    {
        if (_truncated)
        {
            return;
        }
        if (_fullLog.Length > MaxScriptLogBytes)
        {
            _truncated = true;
            _fullLog.AppendLine("（脚本日志超过 20MB，已截断尾部）");
            return;
        }
        _fullLog.AppendLine(line);
    }

    public void CompleteAttempt()
    {
        _attemptSegments.Add(_fullLog.ToString(_attemptStart, _fullLog.Length - _attemptStart));
    }
}
