using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications.Contracts;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Execution;

internal sealed partial class ExecutionRunner
{
    /// <summary>按用户串行运行脚本，保留配置门禁、重试和历史落盘顺序。</summary>
    private async Task<List<RunRecord>> RunUsersAsync(
        RunningExecution exec,
        ScriptInstance script,
        string queueId,
        string queueName,
        IReadOnlyList<ResolvedScriptUser> users,
        ResolvedScriptSpec? resolvedSpec = null,
        Func<int, string>? queueFollowingWork = null,
        Func<int, (string Relation, string LaunchOwner)>? queueNextWork = null,
        bool suppressNotifications = false)
    {
        var records = new List<RunRecord>();
        Dictionary<string, int>? successfulRunsByUser = null;
        DateTime successfulRunsDate = DateTime.MinValue;
        for (int userIndex = 0; userIndex < users.Count; userIndex++)
        {
            ResolvedScriptUser runUser = users[userIndex];
            if (exec.Cts.IsCancellationRequested)
            {
                exec.Status = "cancelled";
                break;
            }
            string recordId = Guid.NewGuid().ToString("N");
            if (!exec.BeginLogSegment(recordId, null, null, recordId))
            {
                exec.Status = "cancelled";
                break;
            }
            // The record's preparation segment is visible immediately. Each later
            // attempt gets a new generation; process and hook callbacks carry the
            // attempt number captured at their source so late output stays old.
            object logSegmentSync = new();
            string activeLogSegmentId = recordId;
            string displayName = $"{script.Name}（{runUser.UserName}）";
            exec.CurrentScriptName = displayName;
            exec.SetPreviewWaiting(script);
            exec.CurrentStatus = "等待开始";

            using ScriptConfigGate.Lease gate = ScriptConfigGate.Get(script.Id);
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
                ExecutionResourceSet itemResources = ExecutionResourceSetBuilder.Build(
                    new[] { new ExecutionResourceInput(script.Id,
                        runUser.Spec is { Succeeded: true } frozen ? frozen.Script : script,
                        new[] { runUser.UserName }, new[] { runUser }) });
                string? quarantine = _state?.FindRecoveryIsolationConflict(itemResources);
                if (quarantine is not null)
                {
                    skippedRecord = CreateQuarantineSkippedRecord(script, exec.Mode,
                        queueId, queueName, runUser, quarantine);
                    skippedRecord.Id = recordId;
                }
                int maxSuccessfulRuns = runUser.Binding.MaxSuccessfulRunsPerDay;
                if (skippedRecord is null && maxSuccessfulRuns > 0)
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
                    if (ExecutionUserEligibility.HasReachedDailySuccessCap(runUser, successfulRuns))
                    {
                        skippedRecord = CreateDailyCapSkippedRecord(
                            script,
                            exec.Mode,
                            queueId,
                            queueName,
                            runUser,
                            successfulRuns,
                            maxSuccessfulRuns);
                        skippedRecord.Id = recordId;
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
                    // 按用户绑定输入实例化的专项快照成功时，以其 Script（实例化后的配置路径与启动参数）运行；
                    // 失败快照由 coordinator 的用户级检查生成失败记录。
                    ScriptInstance userScript = runUser.Spec is { Succeeded: true } userSpec ? userSpec.Script : script;
                    (string nextRelation, string nextOwner) = queueNextWork?.Invoke(userIndex)
                        ?? ("unknown", "unknown");
                    session = new ExecutionCoordinator(
                        userScript, exec.Mode, queueId, queueName, runUser.UserName,
                        exec.Cts.Token,
                        (attempt, max) =>
                        {
                            exec.CurrentAttempt = attempt;
                            exec.CurrentMaxAttempts = max;
                        },
                        status => exec.CurrentStatus = status,
                        (line, level) =>
                        {
                            lock (logSegmentSync) exec.AppendLog(level, line, activeLogSegmentId);
                        },
                        target => exec.SetPreviewTarget(target),
                        _users,
                        _emulatorSupportProviders,
                        runUser,
                        runUser.Spec ?? resolvedSpec,
                        _http,
                        queueFollowingWork?.Invoke(userIndex) ?? "unknown",
                        recordId,
                        (line, level, attempt) => exec.AppendLog(level, line, $"{recordId}:attempt:{attempt}"),
                        (attempt, max) =>
                        {
                            lock (logSegmentSync)
                            {
                                string nextSegment = $"{recordId}:attempt:{attempt}";
                                if (exec.BeginLogSegment(nextSegment, attempt, max, recordId)) activeLogSegmentId = nextSegment;
                            }
                        },
                        nextRelation,
                        nextOwner,
                        _pluginAvailability as IPluginExecutionProviderResolver ?? _plugins);
                    session.TaskReportChanged = exec.UpdateTaskReport;
                    session.OriginExecutionId = exec.Id;
                    session.CancellationMilestoneChanged = exec.MarkCancellationMilestone;
                    if (_history is ITaskHistoryCheckpoints checkpoints)
                        session.TaskCheckpointChanged = checkpoint =>
                        {
                            try { checkpoints.SaveTaskCheckpoint(checkpoint); }
                            catch (Exception ex) { exec.SetPersistenceWarning("Task checkpoint: " + ex.GetType().Name); }
                        };

                    try
                    {
                        record = await session.RunAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Coordinator 异常也必须形成可查询的失败历史，并继续队列后续任务。
                        record = CreateHostErrorRecord(script, exec.Mode, queueId, queueName, runUser.UserName, ex);
                        record.Id = recordId;
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
                        displayName,
                        requireDurable: skippedRecord.ResultCode == "run.not_started_quarantined");
                }
                else
                {
                    if (record is null)
                    {
                        throw new InvalidOperationException($"脚本「{displayName}」未生成运行记录");
                    }
                    publishedRecord = PersistRecord(
                        exec,
                        record,
                        session?.AttemptLogs ?? new List<string>(),
                        session?.ScreenshotStore.SnapshotForHistory() ?? Array.Empty<RunScreenshot>(),
                        displayName);
                    if (successfulRunsByUser is not null
                        && string.Equals(publishedRecord.Status, "success", StringComparison.OrdinalIgnoreCase))
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
                    "partial" => "部分完成",
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
                if (!suppressNotifications) await NotifyScriptIfEnabledAsync(
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
}
