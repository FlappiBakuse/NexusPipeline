using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Notification;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 执行生命周期运行器：承载脚本/队列后台任务、用户串行执行、历史落盘和完成通知。
/// 队列之间的并行准入与完成意图由 ExecutionStateStore/SystemActionExecutor 统一协调。
/// </summary>
internal sealed class ExecutionRunner
{
    private readonly IUserRepository _users;
    private readonly IHistoryStore _history;
    private readonly INotificationService _notifications;
    private readonly SystemActionExecutor _systemActions;
    private readonly IPluginAvailability _pluginAvailability;
    private readonly IUserRunStartingPublisher? _userRunEvents;
    private readonly PluginManager? _plugins;

    public ExecutionRunner(
        IUserRepository users,
        IHistoryStore history,
        INotificationService notifications,
        SystemActionExecutor systemActions,
        IPluginAvailability pluginAvailability,
        IUserRunStartingPublisher? userRunEvents = null,
        PluginManager? plugins = null)
    {
        _users = users;
        _history = history;
        _notifications = notifications;
        _systemActions = systemActions;
        _pluginAvailability = pluginAvailability ?? throw new ArgumentNullException(nameof(pluginAvailability));
        _userRunEvents = userRunEvents;
        _plugins = plugins;
    }

    public async Task RunScriptAsync(RunningExecution exec, ScriptExecutionPlan plan)
    {
        ScriptInstance script = plan.Script;
        try
        {
            exec.SetPreviewWaiting(script);
            string? unavailableReason = PluginAvailability.GetUnavailableReason(
                script.PluginType,
                _pluginAvailability);
            if (unavailableReason is not null)
            {
                List<ResolvedScriptUser> unavailableUsers = ResolvePlanUsers(plan.ResolvedUsers);
                List<RunRecord> skippedRecords = SkipUnavailableScript(
                    exec,
                    script,
                    "",
                    "",
                    unavailableUsers,
                    unavailableReason);
                await NotifyUnavailableScriptAsync(script, unavailableUsers, skippedRecords).ConfigureAwait(false);
                exec.Status = exec.Status == "cancelled" ? "cancelled" : "done";
                return;
            }
            List<ResolvedScriptUser> runUsers = ResolvePlanUsers(plan.ResolvedUsers);
            List<RunRecord> records = await RunUsersAsync(exec, script, "", "", runUsers, plan.ResolvedSpec).ConfigureAwait(false);
            if (records.Count > 0 && records[^1].Status == "cancelled")
            {
                exec.Status = "cancelled";
            }
            else if (exec.Status != "cancelled")
            {
                exec.Status = "done";
            }
        }
        catch (Exception ex)
        {
            exec.Status = "error";
            Logger.Error($"[错误] 脚本「{script.Name}」运行异常：{ex}");
        }
        finally
        {
            exec.ClearPreviewTarget();
            exec.FinishedAt = DateTime.Now;
            _systemActions.CompleteExecution(exec, null);
        }
    }

    /// <summary>按用户串行运行脚本，保留配置门禁、重试和历史落盘顺序。</summary>
    private async Task<List<RunRecord>> RunUsersAsync(
        RunningExecution exec,
        ScriptInstance script,
        string queueId,
        string queueName,
        IReadOnlyList<ResolvedScriptUser> users,
        ResolvedScriptSpec? resolvedSpec = null)
    {
        var records = new List<RunRecord>();
        Dictionary<string, int>? successfulRunsByUser = null;
        DateTime successfulRunsDate = DateTime.MinValue;
        foreach (ResolvedScriptUser runUser in users)
        {
            if (exec.Cts.IsCancellationRequested)
            {
                exec.Status = "cancelled";
                break;
            }
            string displayName = $"{script.Name}（{runUser.UserName}）";
            exec.CurrentScriptName = displayName;
            exec.SetPreviewWaiting(script);
            exec.CurrentStatus = "等待开始";

            SemaphoreSlim gate = ScriptConfigGate.Get(script.Id);
            try
            {
                await gate.WaitAsync(exec.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                exec.Status = "cancelled";
                break;
            }
            ExecutionCoordinator? session = null;
            RunRecord? record = null;
            RunRecord? skippedRecord = null;
            RunRecord? publishedRecord = null;
            NotificationImage? notificationImage = null;
            try
            {
                int maxSuccessfulRuns = runUser.Binding.MaxSuccessfulRunsPerDay;
                if (maxSuccessfulRuns > 0)
                {
                    DateTime today = DateTime.Today;
                    if (successfulRunsByUser is null || successfulRunsDate != today)
                    {
                        successfulRunsByUser = _history
                            .GetSuccessfulRunsByUser(today, script.Id)
                            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                        successfulRunsDate = today;
                    }
                    successfulRunsByUser.TryGetValue(runUser.UserId, out int successfulRuns);
                    if (successfulRuns >= maxSuccessfulRuns)
                    {
                        skippedRecord = CreateDailyCapSkippedRecord(
                            script,
                            exec.Mode,
                            queueId,
                            queueName,
                            runUser,
                            successfulRuns,
                            maxSuccessfulRuns);
                    }
                }

                if (skippedRecord is null)
                {
                    try
                    {
                        _userRunEvents?.Publish(new PluginUserRunStartingEvent(
                            runUser.UserId,
                            runUser.UserName,
                            script.Id,
                            script.Name,
                            queueId,
                            queueName,
                            exec.Mode,
                            DateTimeOffset.Now));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[插件] 用户运行事件发布异常：{ex.Message}");
                    }
                    session = new ExecutionCoordinator(
                        script, exec.Mode, queueId, queueName, runUser.UserName,
                        exec.Cts.Token,
                        (attempt, max) =>
                        {
                            exec.CurrentAttempt = attempt;
                            exec.CurrentMaxAttempts = max;
                        },
                        status => exec.CurrentStatus = status,
                        (line, level) => exec.AppendLog(level, line),
                        target => exec.SetPreviewTarget(target),
                        _users,
                        runUser,
                        resolvedSpec);

                    try
                    {
                        record = await session.RunAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Coordinator 异常也必须形成可查询的失败历史，并继续队列后续任务。
                        record = CreateHostErrorRecord(script, exec.Mode, queueId, queueName, runUser.UserName, ex);
                        record.UserId = runUser.UserId;
                        Logger.Error($"[错误] 脚本「{displayName}」协调器异常，已生成失败历史并继续：{ex}");
                    }
                }
                if (skippedRecord is not null)
                {
                    publishedRecord = PersistRecord(
                        exec,
                        skippedRecord,
                        new List<string>(),
                        Array.Empty<RunScreenshot>(),
                        displayName);
                }
                else
                {
                    if (record is null)
                    {
                        throw new InvalidOperationException($"脚本「{displayName}」未生成运行记录");
                    }
                    if (string.IsNullOrWhiteSpace(record.FinalStatus))
                    {
                        record.FinalStatus = record.Status == "success" ? "success" : record.Status;
                    }
                    publishedRecord = PersistRecord(
                        exec,
                        record,
                        session?.AttemptLogs ?? new List<string>(),
                        session?.ScreenshotStore.SnapshotForHistory() ?? Array.Empty<RunScreenshot>(),
                        displayName);
                    if (successfulRunsByUser is not null
                        && string.Equals(publishedRecord.FinalStatus, "success", StringComparison.OrdinalIgnoreCase))
                    {
                        successfulRunsByUser[runUser.UserId] = successfulRunsByUser.GetValueOrDefault(runUser.UserId) + 1;
                    }
                }
            }
            finally
            {
                gate.Release();
                if (publishedRecord is null)
                {
                    session?.DisposeScreenshots();
                }
            }

            try
            {
                if (publishedRecord is null)
                {
                    throw new InvalidOperationException($"脚本「{displayName}」未生成已提交的运行记录");
                }
                if (session is not null && record is not null)
                {
                    RunScreenshot? selected = session.ScreenshotStore.SelectForNotification(
                        record.Attempts,
                        record.NotifyScreenshotId);
                    if (selected is not null)
                    {
                        notificationImage = new NotificationImage(
                            selected.Id,
                            selected.FileName,
                            "image/jpeg",
                            selected.Data,
                            selected.Width,
                            selected.Height,
                            selected.CapturedAt);
                    }
                }
                records.Add(publishedRecord);
                exec.AddRecordAndIncrement(publishedRecord);
                exec.CurrentStatus = publishedRecord.Status switch
                {
                    "success" => "运行成功",
                    "cancelled" => "已取消",
                    "skipped" => "已跳过",
                    _ => "运行失败",
                };
                string statusDetail = publishedRecord.Status == "skipped"
                    ? publishedRecord.ResultDetail
                    : $"最终结果：{publishedRecord.Status}（{publishedRecord.ResultDetail}）";
                Logger.Info($"[{(exec.Mode == "auto" ? "自动" : "手动")}运行] 脚本「{displayName}」{statusDetail}");
                if (publishedRecord.Status == "skipped")
                {
                    exec.AppendLog(LogLevel.Info, $"[{displayName}] {publishedRecord.ResultDetail}");
                }
                await NotifyScriptIfEnabledAsync(
                    script,
                    publishedRecord,
                    runUser.Binding,
                    displayName,
                    notificationImage).ConfigureAwait(false);
            }
            finally
            {
                session?.DisposeScreenshots();
            }
        }
        return records;
    }

    private async Task NotifyScriptIfEnabledAsync(
        ScriptInstance script,
        RunRecord record,
        UserScriptBinding binding,
        string displayName,
        NotificationImage? image = null)
    {
        if (!binding.NotifyEnabled)
        {
            return;
        }
        try
        {
            // 用户绑定通知开关与队列级汇总通知相互独立，按每个用户完成立即发送。
            await _notifications.NotifyScriptAsync(script, record, binding, image).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[通知] 脚本「{displayName}」通知发送异常：{ex.Message}");
        }
    }

    internal static RunRecord CreateDailyCapSkippedRecord(
        ScriptInstance script,
        string mode,
        string queueId,
        string queueName,
        ResolvedScriptUser user,
        int successfulRuns,
        int maxSuccessfulRuns)
    {
        DateTime now = DateTime.Now;
        string detail = $"当天已成功运行 {successfulRuns}/{maxSuccessfulRuns} 次，达到最多成功运行次数，已跳过本次运行";
        return new RunRecord
        {
            ScriptInstanceId = script.Id,
            ScriptName = script.Name,
            QueueId = queueId,
            QueueName = queueName,
            Mode = mode,
            UserName = user.UserName,
            UserId = user.UserId,
            StartTime = now,
            EndTime = now,
            Attempts = 0,
            MaxAttempts = Math.Max(1, script.MaxAttempts),
            Status = "skipped",
            FinalStatus = "skipped",
            ResultDetail = detail,
        };
    }

    private RunRecord PersistRecord(
        RunningExecution exec,
        RunRecord record,
        List<string> attemptLogs,
        IReadOnlyList<RunScreenshot> screenshots,
        string displayName)
    {
        _plugins?.EnrichHistory(record);
        HistorySaveResult result;
        try
        {
            result = _history.Save(record, attemptLogs, screenshots);
        }
        catch (Exception ex)
        {
            string warning = $"保存脚本「{displayName}」运行历史时发生异常：{ex.Message}";
            Logger.Warn($"[警告] {warning}");
            result = new HistorySaveResult(record.Clone(), warning);
        }
        if (!string.IsNullOrWhiteSpace(result.PersistenceWarning))
        {
            exec.SetPersistenceWarning(result.PersistenceWarning);
        }
        // HistoryService 返回的是提交后的快照；通知文本属于运行时字段，需要随最终快照保留。
        result.Record.CustomNotifyText = record.CustomNotifyText;
        result.Record.NotifyScreenshotId = record.NotifyScreenshotId;
        return result.Record;
    }

    internal static RunRecord CreateHostErrorRecord(
        ScriptInstance script,
        string mode,
        string queueId,
        string queueName,
        string? userName,
        Exception exception)
    {
        DateTime now = DateTime.Now;
        string reason = $"宿主协调器异常：{exception.Message}";
        return new RunRecord
        {
            ScriptInstanceId = script.Id,
            ScriptName = script.Name,
            QueueId = queueId,
            QueueName = queueName,
            Mode = mode,
            UserName = userName ?? "",
            StartTime = now,
            EndTime = now,
            Attempts = 1,
            MaxAttempts = Math.Max(1, script.MaxAttempts),
            Status = "failed",
            FinalStatus = "failed",
            ResultDetail = reason,
            AttemptDetails = new List<RunAttempt>
            {
                new()
                {
                    Number = 1,
                    StartTime = now,
                    EndTime = now,
                    Status = "failed",
                    Reason = reason,
                },
            },
        };
    }

    public async Task RunQueueAsync(RunningExecution exec, QueueExecutionPlan plan)
    {
        DispatchQueue queue = plan.Queue;
        CompletionIntent? completionIntent = null;
        try
        {
            var records = new List<RunRecord>();
            List<PlannedQueueTask> tasks = plan.Tasks.ToList();
            for (int i = 0; i < tasks.Count; i++)
            {
                if (exec.Cts.IsCancellationRequested)
                {
                    exec.Status = "cancelled";
                    Logger.Info($"调度队列「{queue.Name}」已被取消，后续任务不再执行。");
                    break;
                }

                PlannedQueueTask planned = tasks[i];
                QueueTask task = planned.Task;
                ScriptInstance? script = planned.Script;
                if (script is null)
                {
                    exec.ClearPreviewTarget();
                    var missing = new RunRecord
                    {
                        ScriptInstanceId = task.ScriptInstanceId,
                        ScriptName = "(脚本实例不存在)",
                        QueueId = queue.Id,
                        QueueName = queue.Name,
                        Mode = exec.Mode,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now,
                        Status = "failed",
                        FinalStatus = "failed",
                        ResultDetail = "脚本实例不存在或已被删除",
                    };
                    RunRecord publishedMissing = PersistRecord(exec, missing, new List<string>(), Array.Empty<RunScreenshot>(), queue.Name);
                    records.Add(publishedMissing);
                    exec.AddRecordAndIncrement(publishedMissing);
                    Logger.Warn($"[警告] 调度队列「{queue.Name}」第 {i + 1} 项引用的脚本实例不存在，已跳过。");
                    continue;
                }

                exec.CurrentScriptName = script.Name;
                exec.SetPreviewWaiting(script);
                exec.CurrentAttempt = 0;
                exec.CurrentStatus = "等待开始";
                string? unavailableReason = PluginAvailability.GetUnavailableReason(
                    script.PluginType,
                    _pluginAvailability);
                if (unavailableReason is not null)
                {
                    List<ResolvedScriptUser> unavailableUsers = ResolvePlanUsers(planned.ResolvedUsers);
                    List<RunRecord> skippedRecords = SkipUnavailableScript(
                        exec,
                        script,
                        queue.Id,
                        queue.Name,
                        unavailableUsers,
                        unavailableReason);
                    records.AddRange(skippedRecords);
                    if (!queue.NotifyEnabled)
                    {
                        await NotifyUnavailableScriptAsync(script, unavailableUsers, skippedRecords).ConfigureAwait(false);
                    }
                    continue;
                }
                List<ResolvedScriptUser> runUsers = ResolvePlanUsers(planned.ResolvedUsers);
                if (runUsers.Count == 0)
                {
                    var skipped = new RunRecord
                    {
                        ScriptInstanceId = script.Id,
                        ScriptName = script.Name,
                        QueueId = queue.Id,
                        QueueName = queue.Name,
                        Mode = exec.Mode,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now,
                        Status = "failed",
                        FinalStatus = "failed",
                        ResultDetail = "脚本实例未配置启用用户，已跳过",
                    };
                    RunRecord publishedSkipped = PersistRecord(exec, skipped, new List<string>(), Array.Empty<RunScreenshot>(), queue.Name);
                    records.Add(publishedSkipped);
                    exec.AddRecordAndIncrement(publishedSkipped);
                    Logger.Warn($"[警告] 调度队列「{queue.Name}」第 {i + 1} 项引用的脚本实例「{script.Name}」未配置启用用户，已跳过。");
                    continue;
                }
                records.AddRange(await RunUsersAsync(exec, script, queue.Id, queue.Name, runUsers, planned.ResolvedSpec).ConfigureAwait(false));
                if (exec.Status == "cancelled")
                {
                    break;
                }
            }
            bool anyCancelled = exec.Status == "cancelled"
                || exec.Cts.IsCancellationRequested
                || records.Any(record => record.Status == "cancelled");
            exec.Status = anyCancelled ? "cancelled" : "done";
            if (queue.NotifyEnabled)
            {
                await _notifications.NotifyQueueAsync(queue, records).ConfigureAwait(false);
            }

            if (!anyCancelled)
            {
                Logger.Info($"调度队列「{queue.Name}」全部任务执行完毕，执行完成操作：{QueueRule.CompletionActionDesc(queue.CompletionAction)}。");
                completionIntent = new CompletionIntent(exec.Id, queue.Name, queue.CompletionAction);
            }
            else
            {
                Logger.Warn($"调度队列「{queue.Name}」未全部完成（有任务被取消），跳过完成操作。");
            }
        }
        catch (Exception ex)
        {
            exec.Status = "error";
            Logger.Error($"[错误] 调度队列「{queue.Name}」运行异常：{ex}");
        }
        finally
        {
            exec.ClearPreviewTarget();
            exec.FinishedAt = DateTime.Now;
            _systemActions.CompleteExecution(exec, completionIntent);
        }
    }

    private async Task NotifyUnavailableScriptAsync(
        ScriptInstance script,
        IReadOnlyList<ResolvedScriptUser> users,
        IReadOnlyList<RunRecord> records)
    {
        if (users.Count == 0)
        {
            return;
        }

        for (int i = 0; i < users.Count && i < records.Count; i++)
        {
            ResolvedScriptUser user = users[i];
            await NotifyScriptIfEnabledAsync(
                script,
                records[i],
                user.Binding,
                $"{script.Name}（{user.UserName}）").ConfigureAwait(false);
        }
    }

    private List<RunRecord> SkipUnavailableScript(
        RunningExecution exec,
        ScriptInstance script,
        string queueId,
        string queueName,
        IReadOnlyList<ResolvedScriptUser> users,
        string unavailableReason)
    {
        string detail = "绑定的" + unavailableReason;
        string logLine = string.IsNullOrWhiteSpace(queueName)
            ? $"[错误] 脚本实例「{script.Name}」{detail}，已跳过本次运行。"
            : $"[错误] 调度队列「{queueName}」中的脚本实例「{script.Name}」{detail}，已跳过本次运行。";
        exec.CurrentScriptName = script.Name;
        exec.CurrentStatus = "已跳过（专项插件不可用）";
        exec.AppendLog(LogLevel.Error, logLine);
        Logger.Error(logLine);

        var records = new List<RunRecord>();
        if (users.Count == 0)
        {
            records.Add(PublishUnavailableRecord(exec, script, queueId, queueName, null, detail));
            return records;
        }

        foreach (ResolvedScriptUser user in users)
        {
            records.Add(PublishUnavailableRecord(exec, script, queueId, queueName, user, detail));
        }
        return records;
    }

    private RunRecord PublishUnavailableRecord(
        RunningExecution exec,
        ScriptInstance script,
        string queueId,
        string queueName,
        ResolvedScriptUser? user,
        string detail)
    {
        DateTime now = DateTime.Now;
        var record = new RunRecord
        {
            ScriptInstanceId = script.Id,
            ScriptName = script.Name,
            QueueId = queueId,
            QueueName = queueName,
            Mode = exec.Mode,
            UserName = user?.UserName ?? "",
            UserId = user?.UserId ?? "",
            StartTime = now,
            EndTime = now,
            Attempts = 1,
            MaxAttempts = Math.Max(1, script.MaxAttempts),
            Status = "failed",
            FinalStatus = "failed",
            ResultDetail = detail,
            AttemptDetails = new List<RunAttempt>
            {
                new()
                {
                    Number = 1,
                    StartTime = now,
                    EndTime = now,
                    Status = "failed",
                    Reason = detail,
                },
            },
        };
        string displayName = string.IsNullOrWhiteSpace(user?.UserName)
            ? script.Name
            : $"{script.Name}（{user.UserName}）";
        RunRecord published = PersistRecord(exec, record, new List<string>(), Array.Empty<RunScreenshot>(), displayName);
        exec.AddRecordAndIncrement(published);
        return published;
    }

    private static List<ResolvedScriptUser> ResolvePlanUsers(IReadOnlyList<ResolvedScriptUser>? resolved)
    {
        return resolved?.ToList() ?? new List<ResolvedScriptUser>();
    }
}
