using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 一次 Attempt 的终局判定状态机（结构拆分，自 ExecutionCoordinator.RunAttemptCoreAsync 抽出）：
/// final judge / marker 宽限 / stall / 进程退出 的状态转移。宿主只调用本类的方法取得「结果或继续等待」，
/// 不直接接触 final judge 的请求/消费细节（归 RuntimeWorkers）。
/// </summary>
internal sealed class AttemptTerminator
{
    private readonly RuntimeWorkers _workers;
    private readonly SessionJudge _judge;
    private readonly Action<string> _statusChanged;

    private bool _terminalObservation;
    private string _terminalFailureReason = "进程退出但未检测到完成标志";

    public AttemptTerminator(RuntimeWorkers workers, SessionJudge judge, Action<string> statusChanged)
    {
        _workers = workers;
        _judge = judge;
        _statusChanged = statusChanged;
    }

    /// <summary>是否已进入终局观察状态（等待最终判断完成的循环内短轮询）。</summary>
    public bool TerminalObservation => _terminalObservation;

    /// <summary>终局判定应用：terminal 观察且最终判断已完成时产生结果（成功/部分完成/失败），否则返回 null。</summary>
    public RunAttemptResult? TryApplyFinalDecision()
    {
        if (!_terminalObservation || !_workers.FinalJudgeCompleted)
        {
            return null;
        }
        if (_judge.IsFailure)
        {
            RunAttemptResult failed = RunAttemptResult.Failed(_judge.Reason ?? "日志出现失败关键字，任务判定失败");
            failed.NotifyText = _judge.NotifyText;
            failed.NotifyScreenshotId = _judge.NotifyScreenshotId;
            return failed;
        }
        if (_judge.IsMarker)
        {
            return CreateMarkerResult("判断脚本判定成功");
        }
        return RunAttemptResult.Failed(_terminalFailureReason);
    }

    /// <summary>将已接受的完成标志转换为 Attempt 结果，保留判断脚本返回的 success/partial 状态与通知字段。</summary>
    public RunAttemptResult CreateMarkerResult(string successFallbackReason)
    {
        RunAttemptResult result = _judge.MarkerOutcome == SessionJudge.JudgeOutcome.Partial
            ? RunAttemptResult.Partial(_judge.Reason ?? "判断脚本判定部分完成")
            : RunAttemptResult.Success(_judge.Reason ?? successFallbackReason);
        result.NotifyText = _judge.NotifyText;
        result.NotifyScreenshotId = _judge.NotifyScreenshotId;
        return result;
    }

    /// <summary>进程退出时的终局状态转移；返回 null 表示已进入最终判定等待（宿主继续短轮询）。</summary>
    public RunAttemptResult? OnScriptExited(bool monitorIsNull, bool logConfigured, bool skipFinalJudge)
    {
        if (_terminalObservation)
        {
            // 已进入最终判定等待状态；由循环顶部统一应用结果。
            return TryApplyFinalDecision();
        }
        RunAttemptResult? result = null;
        bool scriptMode = _judge.ScriptMode;
        if (monitorIsNull && logConfigured)
        {
            if (scriptMode && !skipFinalJudge)
            {
                _statusChanged?.Invoke("脚本已退出，触发判断脚本最终判定...");
                RequestFinalJudge("已配置日志路径但未找到日志文件，进程退出且未检测到完成标志");
            }
            else
            {
                result = RunAttemptResult.Failed("已配置日志路径但未找到日志文件，进程退出且未检测到完成标志");
            }
        }
        else if (monitorIsNull)
        {
            if (scriptMode && !skipFinalJudge)
            {
                _statusChanged?.Invoke("脚本已退出，触发判断脚本最终判定...");
                RequestFinalJudge("未配置日志路径，判断脚本无法触发，进程已退出");
            }
            else if (scriptMode)
            {
                result = RunAttemptResult.Failed("未配置日志路径，判断脚本无法触发，进程已退出");
            }
            else
            {
                result = RunAttemptResult.Success("进程自行退出（未配置日志监控，按退出判定成功）");
            }
        }
        else if (_judge.IsFailure)
        {
            result = RunAttemptResult.Failed(_judge.Reason ?? "日志出现失败关键字，任务判定失败");
            result.NotifyText = _judge.NotifyText;
            result.NotifyScreenshotId = _judge.NotifyScreenshotId;
        }
        else if (_judge.IsMarker)
        {
            result = CreateMarkerResult("日志出现完成标志，脚本正常运行结束");
        }
        else if (_judge.IsConfigured)
        {
            if (scriptMode && !skipFinalJudge)
            {
                _statusChanged?.Invoke("脚本已退出，触发判断脚本最终判定...");
                RequestFinalJudge("进程退出但未检测到完成标志");
            }
            else
            {
                result = RunAttemptResult.Failed("进程退出但未检测到完成标志");
            }
        }
        else
        {
            result = RunAttemptResult.Success("进程自行退出（未配置完成标志，按退出判定成功）");
        }
        return TryApplyFinalDecision() ?? result;
    }

    /// <summary>日志 stall 的终局状态转移：脚本模式触发最终判定，否则直接失败；返回 null 表示已进入等待。</summary>
    public RunAttemptResult? OnStall(StallObservation stall, bool skipFinalJudge)
    {
        RunAttemptResult? result = null;
        if (_judge.ScriptMode && !skipFinalJudge)
        {
            _statusChanged?.Invoke("日志超时，触发判断脚本最终判定...");
            RequestFinalJudge(stall.Reason);
        }
        else
        {
            result = RunAttemptResult.Failed(stall.Reason);
        }
        return TryApplyFinalDecision() ?? result;
    }

    private void RequestFinalJudge(string fallbackReason)
    {
        _terminalObservation = true;
        _terminalFailureReason = fallbackReason;
        _workers.RequestFinalJudge();
    }
}
