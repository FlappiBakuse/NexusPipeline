using System.Diagnostics;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>一次 Attempt 的监控循环结果；保留循环结束时仍需由协调器释放的日志监视器。</summary>
internal sealed record AttemptMonitorLoopResult(RunAttemptResult Result, LogMonitor? Monitor);

/// <summary>
/// 一次 Attempt 的时序监控边界：消费 worker 结果、读取日志、触发周期/终局判断、观察进程退出与 stall。
/// 该类型只搬运原协调器循环，状态转移仍由 RuntimeWorkers、AttemptTerminator 和 RunSession 负责。
/// </summary>
internal sealed class AttemptMonitorLoop
{
    private const int ExitGraceSecondsAfterMarker = 60;

    public async Task<AttemptMonitorLoopResult> RunAsync(
        RunSession session,
        RunAttempt attempt,
        string modeText,
        string attemptId,
        DateTime attemptStart,
        Process process,
        string launchExe,
        string? excludeGame,
        AttemptLogEnvironment logEnv,
        LogMonitor? monitor,
        AttemptMonitor attemptMonitor,
        SessionJudge judge,
        bool scriptMode,
        RuntimeWorkers workers,
        AttemptTerminator terminator,
        RunScreenshotStore screenshotStore,
        Func<CancellationToken> operationToken,
        Func<bool> budgetExpired,
        Func<bool> killScriptAndConfirm,
        Action bringGameToFrontIfRunning,
        Action<int> scheduleRecentPcScreenshot)
    {
        var state = new MonitorState(monitor);
        try
        {
            while (state.Result is null)
            {
                CancellationToken token = operationToken();
                token.ThrowIfCancellationRequested();

                // 先预热最近有效帧，再消费判断结果和新增日志；截图请求可能就在本轮随后到达。
                // 模拟器模式跳过（模拟器截图走实时 ADB，不使用 PC 窗口缓存）。
                if (!EmulatorSupport.IsEmulator(session.Script))
                {
                    bringGameToFrontIfRunning();
                    scheduleRecentPcScreenshot(attempt.Number);
                }

                workers.ConsumeConfigSyncResult();
                await workers.ConsumeJudgeResultAsync().ConfigureAwait(false);
                workers.TryQueuePendingFinalJudge();
                state.Result = terminator.TryApplyFinalDecision();
                if (state.Result is not null)
                {
                    break;
                }

                // 自动更新配置首次检测——仅第 1 次尝试、运行开始 15 秒（缩放）后同步一次
                // config → store（捕获脚本启动后自行更新的任务配置；重试轮不检测）。
                // 并入主循环避免后台任务与收尾还原的竞态；关/开模式共有。
                if (!session.FirstSyncDone && attempt.Number == 1 && session.ConfigRun is not null && session.ConfigRun.IsPrepared
                    && RunSession.ShouldRunFirstSync(session.Budget!.ElapsedSeconds, TestHooks.ScaledSeconds(15)))
                {
                    session.FirstSyncDone = true;
                    Logger.Info($"[{modeText}运行] 脚本「{session.Script.Name}」自动更新配置首次检测（运行开始 15 秒后）。");
                    if (!workers.TryStartConfigSync(new ConfigSyncRequest(attemptId, attempt.Number, true, DateTime.Now)))
                    {
                        Logger.Info($"[{modeText}运行] 脚本「{session.Script.Name}」配置同步 worker 当前繁忙，本次首次检测已并入已有同步。");
                    }
                }

                if (judge.IsFailure)
                {
                    if (killScriptAndConfirm())
                    {
                        state.Result = RunAttemptResult.Failed(judge.Reason ?? "日志出现失败关键字，任务判定失败");
                        state.Result.NotifyText = judge.NotifyText;
                        state.Result.NotifyScreenshotId = judge.NotifyScreenshotId;
                    }
                    else
                    {
                        state.Result = RunAttemptResult.Fatal("脚本进程清理未确认，已阻断配置替换与重试");
                    }
                    break;
                }

                if (session.Script.TotalTimeoutMinutes > 0
                    && session.Budget!.IsExpired)
                {
                    state.Result = RunAttemptResult.Fatal($"运行总时间超过限制（{session.Script.TotalTimeoutMinutes} 分钟）");
                    break;
                }

                state.Monitor = logEnv.RefreshMonitor(state.Monitor);

                string newContent = attemptMonitor.ReadLog(state.Monitor);
                if (newContent.Length > 0)
                {
                    state.FirstEntryAt ??= DateTime.Now;
                    if (state.StallStatusShown)
                    {
                        // 日志恢复更新后立即清除「日志无新内容」提示，避免调度卡片误导到判定/退出才复原。
                        state.StallStatusShown = false;
                        session.ReportStatus("任务运行中");
                    }
                    foreach (string line in newContent.Split('\n').Select(l => l.TrimEnd('\r')))
                    {
                        if (line.Trim().Length == 0)
                        {
                            continue;
                        }
                        session.ReportLogLine(line, LogLevelUtil.ParseObserved(line));
                        session.AppendScriptLogLine(line);
                        SessionJudge.LineHit lineHit = judge.HandleLine(line);
                        bool effectiveKeywordHit = lineHit switch
                        {
                            SessionJudge.LineHit.SuccessKeyword => judge.IsMarker && !judge.IsFailure,
                            SessionJudge.LineHit.FailureKeyword => judge.IsFailure,
                            _ => false,
                        };
                        if (effectiveKeywordHit && !state.KeywordScreenshotCaptured)
                        {
                            state.KeywordScreenshotCaptured = true;
                            await screenshotStore.CaptureAsync(
                                attempt.Number,
                                lineHit == SessionJudge.LineHit.FailureKeyword ? "keyword-failed" : "keyword-success",
                                operationToken()).ConfigureAwait(false);
                        }
                        switch (lineHit)
                        {
                            case SessionJudge.LineHit.SuccessKeyword:
                                session.ReportStatus("已检测到成功关键字，等待脚本退出...");
                                Logger.Info($"[{modeText}运行] 脚本「{session.Script.Name}」日志出现成功关键字。");
                                break;
                            case SessionJudge.LineHit.FailureKeyword:
                                session.ReportStatus("已检测到失败关键字，任务判定失败");
                                Logger.Info($"[{modeText}运行] 脚本「{session.Script.Name}」日志出现失败关键字，任务判定失败。");
                                break;
                        }
                    }
                }

                // （台账外，修正）：周期触发与退出/stall 最终触发同轮先后命中时跳过最终触发——
                // 周期触发输入为「无新内容」状态，同轮内日志段不变，最终触发属完全重复执行；
                // 批次触发（有新内容）后的同轮最终触发**必须保留**（进程退出是新事实，判断脚本可能
                // 基于自身状态文件在第二次执行给出最终判定，如计数器——06 spec「进程退出时最终触发」用例）。
                bool skipFinalJudge = false;
                if (scriptMode && newContent.Length > 0 && state.Result is null && !judge.IsMarker)
                {
                    workers.QueueJudge(final: false);
                }
                else if (scriptMode && newContent.Length == 0 && state.Result is null
                    && state.FirstEntryAt is not null && !judge.IsMarker && (DateTime.Now - judge.LastJudgeAt).TotalSeconds >= TestHooks.ScaledSeconds(30))
                {
                    state.StallStatusShown = true;
                    session.ReportStatus("日志无新内容，周期触发判断脚本...");
                    skipFinalJudge = true;
                    workers.QueueJudge(final: false);
                }

                await workers.ConsumeJudgeResultAsync().ConfigureAwait(false);
                state.Result = terminator.TryApplyFinalDecision();
                if (state.Result is not null)
                {
                    break;
                }

                bool scriptExited = attemptMonitor.IsScriptExited(process, launchExe, session.ProcessOwnership, excludeGame);
                if (scriptExited)
                {
                    state.Result = terminator.OnScriptExited(state.Monitor is null, !string.IsNullOrWhiteSpace(session.Script.LogPath), skipFinalJudge);
                    if (state.Result is not null)
                    {
                        break;
                    }
                }

                if (terminator.TerminalObservation)
                {
                    await Task.Delay(TestHooks.ScaledMs(50), operationToken()).ConfigureAwait(false);
                    continue;
                }

                if (!judge.IsMarker)
                {
                    StallObservation stall = attemptMonitor.CheckStall(
                        state.Monitor,
                        !string.IsNullOrWhiteSpace(session.Script.LogPath),
                        attemptStart,
                        state.FirstEntryAt,
                        session.Script.LogStallTimeoutMinutes);
                    if (stall.Hit)
                    {
                        state.Result = terminator.OnStall(stall, skipFinalJudge);
                        if (state.Result is not null)
                        {
                            break;
                        }
                    }
                }

                if (judge.IsMarker
                    && (DateTime.Now - judge.MarkerSeenAt!.Value).TotalSeconds >= TestHooks.ScaledSeconds(ExitGraceSecondsAfterMarker))
                {
                    state.Result = killScriptAndConfirm()
                        ? terminator.CreateMarkerResult("完成标志已出现，等待退出超时后已终止脚本，判定成功")
                        : RunAttemptResult.Fatal("脚本进程清理未确认，已阻断配置替换与重试");
                    break;
                }

                await Task.Delay(TestHooks.ScaledMs(1000), operationToken()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (budgetExpired() || session.Budget?.IsExpired == true)
        {
            state.Result = RunAttemptResult.Fatal($"运行总时间超过限制（{session.Script.TotalTimeoutMinutes} 分钟）");
        }
        catch (OperationCanceledException)
        {
            state.Result = RunAttemptResult.Cancelled("运行已取消");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 脚本「{session.Script.Name}」监控异常：{ex.Message}");
            state.Result = RunAttemptResult.Failed($"监控异常：{ex.Message}");
        }

        return new AttemptMonitorLoopResult(
            state.Result ?? RunAttemptResult.Failed("未知原因：未能取得运行结果"),
            state.Monitor);
    }

    private sealed class MonitorState
    {
        public MonitorState(LogMonitor? monitor)
        {
            Monitor = monitor;
        }

        public LogMonitor? Monitor { get; set; }
        public bool StallStatusShown { get; set; }
        public bool KeywordScreenshotCaptured { get; set; }
        public DateTime? FirstEntryAt { get; set; }
        public RunAttemptResult? Result { get; set; }
    }
}
