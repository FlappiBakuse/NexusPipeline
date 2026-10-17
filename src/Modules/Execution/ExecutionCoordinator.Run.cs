using System.Diagnostics;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Execution.Runtime;
using NexusPipeline.Modules.Execution.Targets;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Modules.Configuration.Paths;

namespace NexusPipeline.Modules.Execution;

internal sealed partial class ExecutionCoordinator : RunSession
{
    public async Task<RunRecord> RunAsync()
    {
        _budget = new RunBudget(_script.TotalTimeoutMinutes, DateTime.Now);
        var record = new RunRecord
        {
            Id = _recordId ?? Guid.NewGuid().ToString("N"),
            ScriptInstanceId = _script.Id,
            ScriptName = _script.Name,
            QueueId = _queueId,
            QueueName = _queueName,
            Mode = _mode,
            UserName = _userName ?? "",
            UserId = _resolvedUser?.UserId ?? "",
            PluginVersion = _resolvedSpec?.PluginVersion ?? "",
            ProfileHash = _resolvedSpec?.ProfileHash ?? "",
            JudgeSourceKind = _resolvedSpec?.JudgeScript.SourceKind ?? "",
            JudgeHash = _resolvedSpec?.JudgeScript.ContentHash ?? "",
            StartTime = DateTime.Now,
            ResultCode = "run.running",
        };
        _providerRecordId = record.Id;

        ResolvedScriptUser? resolvedUser = _resolvedUser
            ?? (string.IsNullOrWhiteSpace(_userName)
                ? null
                : _users.ResolveEnabledBinding(_script, _userName));
        ResolvedScriptUser? user = resolvedUser;
        if (!string.IsNullOrWhiteSpace(_userName) && user is null)
        {
            record.Status = "failed";
            record.EndTime = DateTime.Now;
            record.ResultDetail = $"用户「{_userName}」不存在或已禁用";
            record.ResultCode = "run.user_unavailable";
            record.ResultArgs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["user"] = _userName,
            };
            return record;
        }
        if (_script.RequiresReconfiguration || (_script.ConfigurationRevision.Length > 0
            && (user is null || !NexusPipeline.Modules.Configuration.Snapshots.ConfigSnapshotService.HasSnapshot(_script.Id, user.UserId))))
        {
            record.Status = "failed";
            record.EndTime = DateTime.Now;
            record.ResultDetail = "插件包含破坏性配置更新，请重新设置并保存脚本实例";
            record.ResultCode = "run.configuration_setup_required";
            return record;
        }
        if (_resolvedUser?.Spec is { Succeeded: false } userSpec)
        {
            // 按用户绑定输入解析的专项快照失败（如绑定的配置文件已被改名或删除）：只影响该用户，
            // 在编辑配置中重新选择接管配置即可恢复。
            record.Status = "failed";
            record.EndTime = DateTime.Now;
            record.ResultDetail = userSpec.Error ?? "脚本配置解析失败";
            record.ResultCode = "run.spec_failed";
            return record;
        }
        if (_resolvedSpec is { ConfigInputCandidates.Count: >= 2 }
            || _resolvedSpec is { RequiredConfigRelativePath.Length: > 0, ConfigInputValue.Length: 0 })
        {
            // 接管的配置文件/实例目录尚未选定：目录型 configPath 在未定时会解析为存在的目录，
            // 继续运行会把整个目录采用为用户快照，必须先在编辑配置中选择。
            record.Status = "failed";
            record.EndTime = DateTime.Now;
            record.ResultDetail = "当前脚本目录存在多个配置，请先编辑配置选择要接管的配置";
            record.ResultCode = "run.config_selection_required";
            return record;
        }
        if (user is not null)
        {
            record.UserName = user.UserName;
        }
        _activeUser = user;
        if (_resolvedSpec?.TaskProtocol is not null)
            TaskProtocolRun = new TaskProtocolRun(_resolvedSpec, record.Id, record.UserId,
                executionContext: CreateTaskExecutionContext(record.UserId, "pre_launch")) { Changed = report =>
            {
                var checkpoint = record.Clone(); checkpoint.TaskReport = report;
                TaskCheckpointChanged?.Invoke(checkpoint);
                TaskReportChanged?.Invoke(report);
            } };

        _budgetExpiryCts = new CancellationTokenSource();
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_token, _budgetExpiryCts.Token);
        _budgetWatchdog = new RunBudgetWatchdog(_budget, _token, () =>
        {
            _budgetExpired = true;
            try
            {
                _budgetExpiryCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        });
        _budgetWatchdog.Start();

        var retryPolicy = new RetryPolicy(_script.MaxAttempts);
        int maxAttempts = retryPolicy.MaxAttempts;
        record.MaxAttempts = maxAttempts;
        _attemptChanged?.Invoke(1, maxAttempts);

        try
        {
            // 配置运行会话在 try 内创建并准备，任何 Prepare 异常都统一进入幂等 FinalizeRun。
            _configRun = new ConfigRunSession(
                _script.Id,
                resolvedUser?.UserKey,
                _script.ConfigPath,
                _script.HasJudgeScript() && TaskProtocolRun is null,
                _resolvedSpec,
                OriginExecutionId,
                record.Id);
            if (TaskProtocolRun is not null) _configRun.RestoreTaskSelections = TaskProtocolRun.Restore;
            _configRun.PrepareScriptArea();
            if (user is not null && (!string.IsNullOrWhiteSpace(_script.ConfigPath) || _resolvedSpec?.ProviderPlan is not null))
            {
                _statusChanged?.Invoke("正在加载用户配置...");
                if (!_configRun.Prepare(out string? prepError))
                {
                    record.Status = "failed";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = $"用户配置加载失败：{prepError}";
                    record.ResultCode = "run.user_config_load_failed";
                    Logger.Error($"[错误] 脚本「{_script.Name}」用户「{user.UserName}」配置加载失败：{prepError}");
                    return record;
                }
            }

            if (TaskProtocolRun is not null)
            {
                try
                {
                    await TaskProtocolRun.PreflightAsync(OperationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    record.Status = "cancelled";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = "任务发现已取消";
                    record.ResultCode = "run.cancelled";
                    return record;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[专项任务] 配置发现失败：{ex.GetType().Name}");
                    record.Status = "failed";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = "无法建立可信任务计划";
                    record.ResultCode = "tasks.discovery_failed";
                    return record;
                }
                if (TaskProtocolRun.IsAdmissionBlocked)
                {
                    record.Status = "blocked";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = "配置检查未通过，未启动脚本或游戏";
                    record.ResultCode = "tasks.admission_blocked";
                    record.TaskReport = TaskProtocolRun.Snapshot();
                    return record;
                }
            }

            for (int attemptNo = 1; attemptNo <= maxAttempts; attemptNo++)
            {
                _attemptStarted?.Invoke(attemptNo, maxAttempts);
                _attemptChanged?.Invoke(attemptNo, maxAttempts);
                if (attemptNo > 1 && _configRun.IsPrepared && TaskProtocolRun is null)
                {
                    string? retryError = _configRun.PrepareForRetry();
                    if (retryError is not null)
                    {
                        _attemptLogStart = Results.Length;
                        var retryAttempt = new RunAttempt
                        {
                            Number = attemptNo,
                            StartTime = DateTime.Now,
                            EndTime = DateTime.Now,
                            Status = "failed",
                            Reason = "重试前配置交换失败：" + retryError,
                            ReasonCode = "run.retry_prepare_failed",
                        };
                        AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 开始（{retryAttempt.StartTime:HH:mm:ss}） =====");
                        AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 结束：failed（{retryAttempt.Reason}） =====");
                        record.AttemptDetails.Add(retryAttempt);
                        Results.CompleteAttempt();
                        record.Attempts = attemptNo;
                        record.Status = "failed";
                        record.EndTime = retryAttempt.EndTime;
                        record.ResultDetail = retryAttempt.Reason;
                        record.ResultCode = retryAttempt.ReasonCode;
                        break;
                    }
                }
                var attempt = new RunAttempt
                {
                    Number = attemptNo,
                    StartTime = DateTime.Now,
                };
                record.AttemptDetails.Add(attempt);
                // 段起点设置在「开始」头之前——此前段含「结束」头不含「开始」头（首尾不对称），
                // 判断脚本输入与按尝试分批落盘的日志段现在从「开始」头起算。
                _attemptLogStart = Results.Length;
                AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 开始（{attempt.StartTime:HH:mm:ss}） =====");
                _screenshotCapture.BeginAttempt(attemptNo);

                Logger.Info($"===== 脚本「{_script.Name}」第 {attemptNo}/{maxAttempts} 次尝试 =====");
                RunAttemptResult result;
                bool runPreRun = AttemptLifecycle.ShouldRunPreRun(
                    user is not null && !string.IsNullOrWhiteSpace(user.Binding.PreRunScript),
                    user?.Binding.PreRunOnceOnly ?? false,
                    _preRunCompletedSuccessfully);
                bool mainExecuted = true;
                if (runPreRun)
                {
                    RunAttemptResult? preResult = await RunUserScriptCoreAsync(user!.Binding.PreRunScript, "任务前", attempt, OperationToken).ConfigureAwait(false);
                    if (preResult is not null)
                    {
                        // PreRun 只有成功（返回 null）才允许进入 Main；失败/取消直接结束本次 Attempt。
                        mainExecuted = false;
                        result = preResult;
                    }
                    else
                    {
                        _preRunCompletedSuccessfully = true;
                        result = await RunAttemptCoreAsync(attempt).ConfigureAwait(false);
                    }
                }
                else
                {
                    result = await RunAttemptCoreAsync(attempt).ConfigureAwait(false);
                }

                if (_token.IsCancellationRequested && result.Status != "cancelled")
                    result = RunAttemptResult.Cancelled("运行已取消");

                if (mainExecuted && result.Status != "cancelled" && TaskProtocolRun?.IsAdmissionBlocked != true
                    && user is not null && !string.IsNullOrWhiteSpace(user.Binding.PostRunScript)
                    && (TaskProtocolRun is not null ? !user.Binding.PostRunOnFinalOnly : AttemptLifecycle.ShouldRunPostRun(
                        user.Binding.PostRunOnFinalOnly,
                        attemptNo,
                        retryPolicy,
                        result)))
                {
                    RunAttemptResult? postResult = await RunUserScriptCoreAsync(user!.Binding.PostRunScript, "任务后", attempt, OperationToken).ConfigureAwait(false);
                    if (postResult is not null)
                    {
                        result = RunAttemptResult.MergePostRun(result, postResult);
                    }
                }

                bool taskRetry = false;
                bool retryAdmissionBlocked = TaskProtocolRun?.IsAdmissionBlocked == true
                    && !TaskProtocolRun.AdmissionBlockedBeforeAttempt
                    && result.ReasonCode == "tasks.admission_blocked";
                if (TaskProtocolRun is not null)
                {
                    result = TaskProtocolRun.Finish(result, attemptNo);
                    if (!result.IsFatal && result.Status is not ("success" or "skipped" or "cancelled"))
                    {
                        try
                        {
                            taskRetry = await TaskProtocolRun.PrepareRetryAsync(maxAttempts, _token.IsCancellationRequested,
                                _budgetExpired || _budget.IsExpired, OperationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[专项任务] 安全重试已停止：{ex.GetType().Name}");
                            taskRetry = false;
                        }
                    }
                    if (!taskRetry && mainExecuted && result.Status != "cancelled" && !TaskProtocolRun.IsAdmissionBlocked
                        && user?.Binding.PostRunOnFinalOnly == true && !string.IsNullOrWhiteSpace(user.Binding.PostRunScript))
                    {
                        var postResult = await RunUserScriptCoreAsync(user.Binding.PostRunScript, "任务后", attempt, OperationToken).ConfigureAwait(false);
                        if (postResult is not null)
                        {
                            result = RunAttemptResult.MergePostRun(result, postResult);
                            TaskProtocolRun.SetFinalLifecycleFailure(result.Status == "cancelled");
                        }
                    }
                }
                if (TaskProtocolRun?.IsAdmissionBlocked == true && TaskProtocolRun.AdmissionBlockedBeforeAttempt)
                {
                    if (runPreRun)
                    {
                        // Keep the hook's real execution and log while making it
                        // clear that no protocol/main attempt was admitted.
                        attempt.EndTime = DateTime.Now;
                        attempt.Status = "blocked";
                        attempt.Reason = "配置检查未通过，未启动脚本或游戏";
                        attempt.ReasonCode = "tasks.admission_blocked";
                        AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 停止：{attempt.Reason} =====");
                    }
                    else record.AttemptDetails.Remove(attempt);
                    record.Attempts = 0;
                    record.Status = "blocked";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = "配置检查未通过，未启动脚本或游戏";
                    record.ResultCode = "tasks.admission_blocked";
                    Results.CompleteAttempt();
                    record.TaskReport = TaskProtocolRun.Snapshot();
                    break;
                }
                if (retryAdmissionBlocked)
                {
                    // The pre-run hook may have executed and its log belongs to
                    // this entry, but BeginAsync rejected the retry before a
                    // second protocol/main attempt started.
                    attempt.EndTime = DateTime.Now;
                    attempt.Status = "blocked";
                    attempt.Reason = "配置检查未通过，未启动脚本或游戏";
                    attempt.ReasonCode = "tasks.admission_blocked";
                    record.Attempts = attemptNo - 1;
                    record.Status = result.Status;
                    record.EndTime = attempt.EndTime;
                    record.ResultDetail = result.Reason;
                    record.ResultCode = result.ReasonCode;
                    record.ResultArgs = new(result.ReasonArgs, StringComparer.Ordinal);
                    AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 停止：{attempt.Reason} =====");
                    Results.CompleteAttempt();
                    record.TaskReport = TaskProtocolRun!.Snapshot();
                    break;
                }
                attempt.EndTime = DateTime.Now;
                attempt.Status = result.Status;
                attempt.Reason = result.Reason;
                attempt.OutputIncomplete = result.OutputIncomplete;
                attempt.ReasonCode = result.ReasonCode;
                attempt.ReasonArgs = new Dictionary<string, string>(result.ReasonArgs, StringComparer.Ordinal);
                record.Attempts = attemptNo;
                record.ResultCode = result.ReasonCode;
                record.ResultArgs = new Dictionary<string, string>(result.ReasonArgs, StringComparer.Ordinal);
                record.NotifyScreenshotId = result.NotifyScreenshotId;
                if (!string.IsNullOrWhiteSpace(result.NotifyText))
                {
                    record.CustomNotifyText = result.NotifyText;
                }
                AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 结束：{result.Status}（{result.Reason}） =====");
                Logger.Info($"第 {attemptNo} 次尝试结束：{result.Status}（{result.Reason}）");
                Results.CompleteAttempt();

                if (TaskProtocolRun is not null)
                {
                    record.Status = result.Status;
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = result.Reason;
                    record.TaskReport = TaskProtocolRun.Snapshot();
                    if (taskRetry) continue;
                    break;
                }
                if (result.Status is "success" or "partial")
                {
                    record.Status = result.Status;
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = result.Status == "partial"
                        ? (string.IsNullOrWhiteSpace(result.Reason) ? "判断脚本判定部分完成" : result.Reason)
                        : attemptNo == 1 ? "一次成功" : $"第 {attemptNo} 次尝试成功";
                    break;
                }
                if (result.IsFatal)
                {
                    record.Status = result.Status;
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = result.Reason;
                    break;
                }
                if (!retryPolicy.ShouldRetry(attemptNo, result))
                {
                    record.Status = "failed";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = $"达到最大尝试次数（{maxAttempts} 次）仍失败，最后原因：{result.Reason}";
                    record.ResultCode = "run.max_attempts";
                    record.ResultArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["maximum"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["reason"] = result.Reason,
                    };
                    break;
                }
            }

            return record;
        }
        finally
        {
            bool configPrepared = _configRun?.RequiresRestoration == true;
            bool recoveryFailed = false;
            if (_budgetWatchdog is not null)
            {
                await _budgetWatchdog.DisposeAsync().ConfigureAwait(false);
                _budgetWatchdog = null;
            }
            _operationCts?.Dispose();
            _operationCts = null;
            _budgetExpiryCts?.Dispose();
            _budgetExpiryCts = null;
            // ConfigRunSession 是唯一的运行收尾入口：自动更新同步 → 插队还原 →
            // 判断脚本目录清理 → 配置交换还原。
            if (_configRun is not null)
            {
                if (_token.IsCancellationRequested)
                    CancellationMilestoneChanged?.Invoke(CancellationMilestone.Restoring, null);
                _configRun.OriginAttempt = record.Attempts;
                string? restoreError = _configRun.FinalizeRun(_script.AutoUpdateConfig);
                if (restoreError is not null)
                {
                    recoveryFailed = true;
                    if (TaskProtocolRun is not null)
                    {
                        record.Status = "failed"; record.ResultCode = "tasks.recovery_conflict";
                        TaskProtocolRun.SetFinalLifecycleFailure(false);
                    }
                    string msg = $"（警告：配置还原失败，现场已保留，详见日志）";
                    record.ResultDetail += msg;
                    Logger.Error($"[错误] 脚本「{_script.Name}」用户「{user?.UserName ?? _userName ?? ""}」配置还原失败：{restoreError}");
                }
                if (_token.IsCancellationRequested)
                    CancellationMilestoneChanged?.Invoke(CancellationMilestone.RestoreFinished,
                        restoreError is null ? "配置恢复完成" : "配置恢复失败，现场已保留");
            }
            // 取消可能在同步恢复期间到达；仍须完成恢复，但不能将该次运行记为正常完成。
            if (_token.IsCancellationRequested && record.Status is "success" or "partial" or "unverified")
            {
                record.Status = "cancelled";
                record.ResultCode = "run.cancelled";
                record.ResultArgs.Clear();
                record.ResultDetail += "（运行收尾期间已取消）";
                TaskProtocolRun?.SetFinalLifecycleFailure(true);
            }
            if (TaskProtocolRun is not null)
            {
                if (record.Status is "failed" or "cancelled" && TaskProtocolRun.Snapshot()?["lifecycleOutcome"]?.GetValue<string>() == "running")
                    TaskProtocolRun.SetFinalLifecycleFailure(record.Status == "cancelled");
                record.TaskReport = TaskProtocolRun.Snapshot();
            }
            if (_providerProjection is not null) record.TaskReport = _providerProjection.Snapshot();
            record.Outcomes = RunOutcomeProjector.Project(record, configPrepared, recoveryFailed);
        }
    }
}
