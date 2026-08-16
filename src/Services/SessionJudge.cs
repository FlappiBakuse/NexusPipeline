using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 完成判定策略（v0.5.0 从 RunSession 拆出）：关键字 / 判断脚本 两模式的判定状态机。
/// 只维护判定状态与输入，不含 IO/日志/进程操作（由调用方经回调与返回值驱动，行为零变化）。
/// 判定优先级：判断脚本（脚本优先，脚本模式下关键字完全不参与判定，v0.6.4 对齐设计语义）→ 成功/失败关键字（组内 AND、组间 OR）→ 无配置按进程退出判定。
/// 关键字 AND 语义（v0.7.1+）：组内关键字在**整个尝试日志中分别出现即命中**（跨行累积，与出现顺序/间隔无关）；
/// 此前为「同一行内全部出现才命中」，跨行分散的关键字永不判定成功。
/// </summary>
internal sealed class SessionJudge
{
    internal enum JudgeMode
    {
        None,
        Keyword,
        JudgeScript,
    }

    /// <summary>日志行命中的判定类型（仅关键字模式的日志行命中会返回非 None）。</summary>
    internal enum LineHit
    {
        None,
        SuccessKeyword,
        FailureKeyword,
    }

    /// <summary>判断脚本结果应用结果。</summary>
    internal enum JudgeOutcome
    {
        None,
        Success,
        Failure,
    }

    private readonly JudgeMode _mode;

    /// <summary>成功关键字分组待匹配词（跨行累积）：本行出现的词从对应组移除，组清空即该组命中（组间 OR）。</summary>
    private readonly List<HashSet<string>> _successPending;

    /// <summary>失败关键字分组待匹配词（同成功）。</summary>
    private readonly List<HashSet<string>> _failurePending;

    private DateTime? _markerSeenAt;

    private DateTime? _failureSeenAt;

    private string? _judgeReason;

    private string? _judgeNotifyText;

    private DateTime _lastJudgeAt = DateTime.Now;

    public SessionJudge(ScriptInstance script)
    {
        bool scriptMode = script.HasJudgeScript();
        var successGroups = scriptMode ? [] : KeywordRule.Parse(script.SuccessKeywords);
        var failureGroups = scriptMode ? [] : KeywordRule.Parse(script.FailureKeywords);
        _successPending = successGroups.Select(group => new HashSet<string>(group, StringComparer.OrdinalIgnoreCase)).ToList();
        _failurePending = failureGroups.Select(group => new HashSet<string>(group, StringComparer.OrdinalIgnoreCase)).ToList();
        _mode = scriptMode
            ? JudgeMode.JudgeScript
            : _successPending.Count > 0 || _failurePending.Count > 0 ? JudgeMode.Keyword : JudgeMode.None;
    }

    /// <summary>已配置任何完成判定（判断脚本/关键字）。</summary>
    public bool IsConfigured => _mode != JudgeMode.None;

    public bool ScriptMode => _mode == JudgeMode.JudgeScript;

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

    /// <summary>处理一行日志的判定输入：关键字模式跨行累积匹配成功/失败组（组内 AND 跨整个日志、组间 OR）。返回命中类型。</summary>
    public LineHit HandleLine(string line)
    {
        if (_markerSeenAt is null && HitAnyGroup(line, _successPending))
        {
            _markerSeenAt = DateTime.Now;
            return LineHit.SuccessKeyword;
        }
        if (_failureSeenAt is null && HitAnyGroup(line, _failurePending))
        {
            _failureSeenAt = DateTime.Now;
            return LineHit.FailureKeyword;
        }
        return LineHit.None;
    }

    /// <summary>
    /// 跨行 AND 匹配（v0.7.1+）：本行出现的词从对应组移除，任一组清空即命中；
    /// 已清空的组保持命中（后续行不改变已判定结果）。组间 OR 语义由此天然成立。
    /// </summary>
    private static bool HitAnyGroup(string line, List<HashSet<string>> pendingGroups)
    {
        bool any = false;
        foreach (HashSet<string> pending in pendingGroups)
        {
            if (pending.Count == 0)
            {
                any = true;
                continue;
            }
            pending.RemoveWhere(word => line.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (pending.Count == 0)
            {
                any = true;
            }
        }
        return any;
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
