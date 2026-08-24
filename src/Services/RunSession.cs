using System.Text;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 一次「脚本实例 × 用户」运行的状态对象。
/// 执行流程由 <see cref="Execution.ExecutionCoordinator"/> 编排；本类型只保存运行元数据、日志片段和生命周期状态。
/// </summary>
internal class RunSession
{
    private readonly ResultCollector _results = new();

    protected readonly ScriptInstance _script;
    protected readonly string _mode;
    protected readonly string _queueId;
    protected readonly string _queueName;
    protected readonly string? _userName;
    protected readonly ResolvedScriptUser? _resolvedUser;
    protected readonly string? _userKey;
    protected readonly CancellationToken _token;
    protected readonly Action<int, int>? _attemptChanged;
    protected readonly Action<string>? _statusChanged;
    protected readonly Action<string>? _logLine;
    protected RunBudget? _budget;
    protected ScriptUser? _activeUser;
    protected bool _gameFronted;
    protected ProcessOwnership? _processOwnership;
    protected List<string>? _pendingReplaceConfigs;
    protected ConfigRunSession? _configRun;
    protected bool _firstSyncDone;
    protected bool _preRunCompletedSuccessfully;

    protected RunSession(ScriptInstance script, string mode, string queueId, string queueName, string? userName, CancellationToken token,
        ResolvedScriptUser? resolvedUser = null,
        Action<int, int>? attemptChanged = null, Action<string>? statusChanged = null, Action<string>? logLine = null)
    {
        _script = script;
        _mode = mode;
        _queueId = queueId;
        _queueName = queueName;
        _userName = userName;
        _resolvedUser = resolvedUser;
        _userKey = resolvedUser?.UserKey ?? userName;
        _token = token;
        _attemptChanged = attemptChanged;
        _statusChanged = statusChanged;
        _logLine = logLine;
    }

    /// <summary>每尝试脚本日志段（按尝试分批落盘）。</summary>
    protected StringBuilder _scriptFullLog => _results.FullLog;
    protected bool _scriptLogTruncated { get => _results.IsTruncated; set => _results.IsTruncated = value; }
    protected int _attemptLogStart { get => _results.AttemptStart; set => _results.AttemptStart = value; }
    protected List<string> _attemptLogSegments => _results.AttemptSegments;

    public List<string> AttemptLogs => _results.AttemptSegments;

    internal ScriptInstance Script => _script;
    internal string Mode => _mode;
    internal string? UserName => _userName;
    internal string? UserKey => _userKey;
    internal ResolvedScriptUser? ResolvedUser => _resolvedUser;
    internal CancellationToken Token => _token;
    internal StringBuilder ScriptFullLog => _results.FullLog;
    internal bool ScriptLogTruncated { get => _results.IsTruncated; set => _results.IsTruncated = value; }
    internal RunBudget? Budget { get => _budget; set => _budget = value; }
    internal ScriptUser? ActiveUser { get => _activeUser; set => _activeUser = value; }
    internal int AttemptLogStart { get => _results.AttemptStart; set => _results.AttemptStart = value; }
    internal ResultCollector Results => _results;
    internal bool GameFronted { get => _gameFronted; set => _gameFronted = value; }
    internal ProcessOwnership? ProcessOwnership { get => _processOwnership; set => _processOwnership = value; }
    internal List<string>? PendingReplaceConfigs { get => _pendingReplaceConfigs; set => _pendingReplaceConfigs = value; }
    internal ConfigRunSession? ConfigRun { get => _configRun; set => _configRun = value; }
    internal bool FirstSyncDone { get => _firstSyncDone; set => _firstSyncDone = value; }
    internal bool PreRunCompletedSuccessfully { get => _preRunCompletedSuccessfully; set => _preRunCompletedSuccessfully = value; }
    internal Action<string>? StatusChanged => _statusChanged;
    internal Action<string>? LogLine => _logLine;

    internal void ReportStatus(string status) => _statusChanged?.Invoke(status);
    internal void ReportLogLine(string line) => _logLine?.Invoke(line);

    /// <summary>自动更新配置首次检测时机判定（纯函数便于单测）。</summary>
    internal static bool ShouldRunFirstSync(double elapsedSeconds, double thresholdSeconds)
    {
        return elapsedSeconds >= thresholdSeconds;
    }

    protected void AppendScriptLog(string line)
    {
        _results.Append(line);
    }
}
