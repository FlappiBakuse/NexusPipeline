using System.Text;

namespace NexusPipeline.Modules.Execution;

/// <summary>运行日志与按尝试日志段收集器，避免执行协调器同时管理日志容量和落盘分段。</summary>
internal sealed class ResultCollector
{
    private const int MaxScriptLogBytes = 20 * 1024 * 1024;

    private readonly StringBuilder _fullLog = new();
    private readonly List<string> _attemptSegments = new();
    private bool _truncated;
    private int _attemptStart;
    private int _fullLogUtf8Bytes;
    private readonly object _sync = new();
    internal int Length { get { lock (_sync) return _fullLog.Length; } }
    internal (string Text, bool Truncated) SnapshotAttemptTail(int maximum)
    {
        lock (_sync)
        {
            int count = Math.Max(0, _fullLog.Length - _attemptStart);
            int length = Math.Min(count, maximum);
            return (_fullLog.ToString(_attemptStart + count - length, length), count > maximum || _truncated);
        }
    }

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
        lock (_sync) AppendLocked(line);
    }
    private void AppendLocked(string line)
    {
        if (_truncated)
        {
            return;
        }
        string lineWithNewLine = line + Environment.NewLine;
        int lineBytes = Encoding.UTF8.GetByteCount(lineWithNewLine);
        if (_fullLogUtf8Bytes + lineBytes > MaxScriptLogBytes)
        {
            _truncated = true;
            const string marker = "（脚本日志超过 20MB，已截断尾部）";
            int markerBytes = Encoding.UTF8.GetByteCount(marker + Environment.NewLine);
            if (_fullLogUtf8Bytes + markerBytes <= MaxScriptLogBytes)
            {
                _fullLog.AppendLine(marker);
                _fullLogUtf8Bytes += markerBytes;
            }
            return;
        }
        _fullLog.Append(lineWithNewLine);
        _fullLogUtf8Bytes += lineBytes;
    }

    public void CompleteAttempt()
    {
        lock (_sync) _attemptSegments.Add(_fullLog.ToString(_attemptStart, _fullLog.Length - _attemptStart));
    }
}
