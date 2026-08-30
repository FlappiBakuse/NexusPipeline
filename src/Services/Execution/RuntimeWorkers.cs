using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 一次 Attempt 的 Judge/配置同步单飞 worker 集合（结构拆分，自 ExecutionCoordinator 抽出）：
/// worker 创建、Generation/AttemptId 过期判定与收拢。保持「单飞不阻塞」语义——监控循环只消费完成结果，不等待 worker。
/// 最终判定（final judge）的请求/排队/完成标志与 Generation 计数在此收敛，宿主只经 TryApplyFinalDecision 读取结果。
/// </summary>
internal sealed class RuntimeWorkers : IAsyncDisposable
{
    private readonly string _attemptId;
    private readonly int _attemptNumber;
    private readonly CancellationToken _operationToken;
    private readonly string _modeText;
    private readonly string _scriptName;
    private readonly SessionJudge _judge;
    private readonly Action<string> _statusChanged;
    private readonly Func<int, JudgeSnapshot> _captureSnapshot;
    private readonly Action<List<string>> _replaceRequested;
    private readonly SingleFlightWorker<JudgeSnapshot, JudgeWorkerResult> _judgeWorker;
    private readonly SingleFlightWorker<ConfigSyncRequest, bool> _configSyncWorker;

    private int _judgeGeneration;
    private int _finalJudgeGeneration = -1;
    private bool _finalJudgeRequested;
    private bool _finalJudgeQueuePending;
    private bool _finalJudgeCompleted;

    /// <summary>最终判定是否已完成（结果由宿主经 AttemptTerminator.TryApplyFinalDecision 应用）。</summary>
    public bool FinalJudgeCompleted => _finalJudgeCompleted;

    public RuntimeWorkers(
        string attemptId,
        int attemptNumber,
        CancellationToken operationToken,
        string modeText,
        string scriptName,
        SessionJudge judge,
        Action<string> statusChanged,
        Func<int, JudgeSnapshot> captureSnapshot,
        Action<List<string>> replaceRequested,
        Action<ConfigSyncRequest> configSync)
    {
        _attemptId = attemptId;
        _attemptNumber = attemptNumber;
        _operationToken = operationToken;
        _modeText = modeText;
        _scriptName = scriptName;
        _judge = judge;
        _statusChanged = statusChanged;
        _captureSnapshot = captureSnapshot;
        _replaceRequested = replaceRequested;
        _judgeWorker = new SingleFlightWorker<JudgeSnapshot, JudgeWorkerResult>(async (snapshot, workerToken) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(workerToken, _operationToken);
            JudgeScriptResult judgeResult = await JudgeScriptRunner.ExecuteAsync(
                snapshot.Script,
                snapshot.InputJson,
                snapshot.Files.Select(file => new JudgeScriptInputFile
                {
                    Root = file.Root,
                    Path = file.Path,
                    Abs = file.Abs,
                }).ToList(),
                snapshot.Script.ConfigPath,
                snapshot.ScriptDir,
                linked.Token).ConfigureAwait(false);
            return new JudgeWorkerResult(
                snapshot.AttemptId,
                snapshot.AttemptNumber,
                snapshot.Generation,
                judgeResult,
                DateTime.Now);
        });
        _configSyncWorker = new SingleFlightWorker<ConfigSyncRequest, bool>((request, _) =>
        {
            configSync(request);
            return Task.FromResult(true);
        });
    }

    public bool TryStartConfigSync(ConfigSyncRequest request) => _configSyncWorker.TryStart(request);

    public bool ConsumeConfigSyncResult()
    {
        if (!_configSyncWorker.TryTakeCompleted(out _, out Exception? error))
        {
            return false;
        }
        if (error is not null)
        {
            Logger.Warn($"[{_modeText}运行] 脚本「{_scriptName}」自动更新配置同步失败：{error.Message}");
        }
        return true;
    }

    public bool ConsumeJudgeResult()
    {
        if (!_judgeWorker.TryTakeCompleted(out JudgeWorkerResult completed, out Exception? error))
        {
            return false;
        }

        bool isFinal = completed.AttemptId == _attemptId
            && completed.AttemptNumber == _attemptNumber
            && completed.Generation == _finalJudgeGeneration;
        if (error is not null)
        {
            Logger.Warn($"[{_modeText}运行] 脚本「{_scriptName}」判断脚本执行错误（视为继续运行）：{error.Message}");
            if (isFinal)
            {
                _finalJudgeCompleted = true;
            }
            return true;
        }

        bool current = completed.AttemptId == _attemptId
            && completed.AttemptNumber == _attemptNumber
            && completed.Generation == _judgeGeneration;
        if (!current)
        {
            Logger.Info($"[{_modeText}运行] 丢弃脚本「{_scriptName}」过期判断结果（AttemptId/Generation 不匹配）。");
            if (isFinal)
            {
                _finalJudgeRequested = false;
                _finalJudgeQueuePending = true;
            }
            return true;
        }

        JudgeScriptResult judgeResult = completed.Result;
        if (judgeResult.JudgeError is not null)
        {
            Logger.Warn($"[{_modeText}运行] 脚本「{_scriptName}」判断脚本执行错误（视为继续运行）：{judgeResult.JudgeError}");
        }
        else
        {
            SessionJudge.JudgeOutcome outcome = _judge.ApplyJudgeResult(
                judgeResult.Status,
                judgeResult.Reason,
                judgeResult.NotifyText,
                judgeResult.ReplaceConfigs,
                replace =>
                {
                    // （P6）：仅记录待替换配置，不立即应用——应用推迟到尝试收尾（杀进程确认退出后），
                    // 消除进程仍运行时复制覆盖 config 的文件占用/半写窗口。
                    _replaceRequested(replace);
                    Logger.Info($"[{_modeText}运行] 脚本「{_scriptName}」判断脚本请求替换配置（{replace.Count} 个文件），收尾后应用并重试。");
                });
            if (outcome == SessionJudge.JudgeOutcome.Success)
            {
                _statusChanged?.Invoke("判断脚本判定成功，等待脚本退出...");
                Logger.Info($"[{_modeText}运行] 脚本「{_scriptName}」判断脚本判定成功：{judgeResult.Reason}");
            }
            else if (outcome == SessionJudge.JudgeOutcome.Failure)
            {
                _statusChanged?.Invoke("判断脚本判定失败");
                Logger.Info($"[{_modeText}运行] 脚本「{_scriptName}」判断脚本判定失败：{judgeResult.Reason}");
            }
        }
        if (isFinal)
        {
            _finalJudgeCompleted = true;
        }
        return true;
    }

    /// <summary>触发判断脚本执行（批次/周期/最终共用）；单飞 worker 忙时返回 false（调用方按 final 语义保留排队）。</summary>
    public bool QueueJudge(bool final)
    {
        int generation = _judgeGeneration + 1;
        JudgeSnapshot snapshot = _captureSnapshot(generation);
        if (!_judgeWorker.TryStart(snapshot))
        {
            if (final)
            {
                _finalJudgeQueuePending = true;
            }
            return false;
        }
        _judgeGeneration = generation;
        _judge.TouchJudge();
        if (final)
        {
            _finalJudgeRequested = true;
            _finalJudgeQueuePending = false;
            _finalJudgeGeneration = generation;
        }
        return true;
    }

    /// <summary>请求最终判定：未请求且未完成时排队执行（只在进程退出/stall 等终局场景调用，防循环）。</summary>
    public void RequestFinalJudge()
    {
        if (!_finalJudgeRequested && !_finalJudgeCompleted)
        {
            QueueJudge(final: true);
        }
    }

    /// <summary>主循环顶部：最终判定曾因过期结果被重置时补队列。</summary>
    public bool TryQueuePendingFinalJudge()
    {
        if (_finalJudgeQueuePending && !_finalJudgeRequested && !_finalJudgeCompleted)
        {
            return QueueJudge(final: true);
        }
        return false;
    }

    /// <summary>先收拢后台 worker（消费残留结果并停止），再允许宿主进入进程清理与配置收尾，防止旧 Attempt 继续写入状态或文件。</summary>
    public async Task StopAsync()
    {
        ConsumeJudgeResult();
        ConsumeConfigSyncResult();
        await _judgeWorker.StopAsync().ConfigureAwait(false);
        await _configSyncWorker.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}