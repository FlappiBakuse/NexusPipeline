using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 完成判定策略（v0.5.0 从 RunSession 拆出）：关键字 / 完成标志 / 判断脚本 三模式的判定状态机。
/// 只维护判定状态与输入，不含 IO/日志/进程操作（由调用方经回调与返回值驱动，行为零变化）。
/// 判定优先级：判断脚本（脚本优先）→ 成功/失败关键字（行内 AND、行间 OR）→ 专用插件固化标志 → 无配置按进程退出判定。
/// </summary>
internal sealed class SessionJudge
{
    public enum JudgeMode
    {
        None,
        Keyword,
        Marker,
        JudgeScript,
    }

    /// <summary>日志行命中的判定类型（仅 Keyword/Marker 模式的日志行命中会返回非 None）。</summary>
    public enum LineHit
    {
        None,
        SuccessKeyword,
        FailureKeyword,
        Marker,
    }

    /// <summary>判断脚本结果应用结果。</summary>
    public enum JudgeOutcome
    {
        None,
        Success,
        Failure,
    }

    private readonly JudgeMode _mode;

    private readonly List<List<string>> _successGroups;

    private readonly List<List<string>> _failureGroups;

    private readonly List<string> _markers;

    private DateTime? _markerSeenAt;

    private DateTime? _failureSeenAt;

    private string? _judgeReason;

    private string? _judgeNotifyText;

    private DateTime _lastJudgeAt = DateTime.Now;

    public SessionJudge(ScriptInstance script)
    {
        _markers = script.MarkerList();
        _successGroups = KeywordRule.Parse(script.SuccessKeywords);
        _failureGroups = KeywordRule.Parse(script.FailureKeywords);
        bool scriptMode = script.HasJudgeScript();
        bool keywordMode = !scriptMode && (_successGroups.Count > 0 || _failureGroups.Count > 0);
        _mode = scriptMode ? JudgeMode.JudgeScript : keywordMode ? JudgeMode.Keyword : _markers.Count > 0 ? JudgeMode.Marker : JudgeMode.None;
    }

    /// <summary>已配置任何完成判定（判断脚本/关键字/固化标志）。</summary>
    public bool IsConfigured => _mode != JudgeMode.None;

    public bool ScriptMode => _mode == JudgeMode.JudgeScript;

    public bool KeywordMode => _mode == JudgeMode.Keyword;

    public DateTime? MarkerSeenAt => _markerSeenAt;

    public DateTime? FailureSeenAt => _failureSeenAt;

    /// <summary>最近一次判断脚本触发时间。</summary>
    public DateTime LastJudgeAt => _lastJudgeAt;

    /// <summary>失败优先判定：失败命中存在且早于（或同时于）成功命中 → 失败成立。</summary>
    public bool IsFailure => _failureSeenAt is not null && (_markerSeenAt is null || _failureSeenAt <= _markerSeenAt);

    public bool IsMarker => _markerSeenAt is not null;

    /// <summary>判断脚本给出的判定原因（关键字/标志命中时为空，由调用方提供默认文案）。</summary>
    public string? Reason => _judgeReason;

    public string NotifyText => _judgeNotifyText ?? "";

    public void TouchJudge()
    {
        _lastJudgeAt = DateTime.Now;
    }

    /// <summary>处理一行日志的判定输入：关键字模式匹配成功/失败组（行内 AND、行间 OR），标志模式匹配完成标志。返回命中类型。</summary>
    public LineHit HandleLine(string line)
    {
        if (KeywordMode)
        {
            if (_markerSeenAt is null && KeywordRule.LineHits(line, _successGroups))
            {
                _markerSeenAt = DateTime.Now;
                return LineHit.SuccessKeyword;
            }
            if (_failureSeenAt is null && KeywordRule.LineHits(line, _failureGroups))
            {
                _failureSeenAt = DateTime.Now;
                return LineHit.FailureKeyword;
            }
            return LineHit.None;
        }
        if (_markers.Count > 0 && _markerSeenAt is null && TextRules.LineHasCompletionMarker(line, _markers))
        {
            _markerSeenAt = DateTime.Now;
            return LineHit.Marker;
        }
        return LineHit.None;
    }

    /// <summary>应用判断脚本结果：success → 成功标记；failed → 失败标记并（首次）执行配置替换回调。返回是否设置了判定。</summary>
    public JudgeOutcome ApplyJudgeResult(string status, string reason, string notifyText, List<string> replaceConfigs, Action<List<string>> onReplace)
    {
        if (status == "success" && _markerSeenAt is null)
        {
            _markerSeenAt = DateTime.Now;
            _judgeReason = "判断脚本判定成功：" + reason;
            _judgeNotifyText = notifyText;
            return JudgeOutcome.Success;
        }
        if (status == "failed" && _failureSeenAt is null)
        {
            _failureSeenAt = DateTime.Now;
            _judgeReason = "判断脚本判定失败：" + reason;
            _judgeNotifyText = notifyText;
            if (replaceConfigs.Count > 0)
            {
                onReplace(replaceConfigs);
            }
            return JudgeOutcome.Failure;
        }
        return JudgeOutcome.None;
    }
}
