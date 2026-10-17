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
            ResultDetail = detail,
            ResultCode = "run.daily_cap",
            ResultArgs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["successful"] = successfulRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maximum"] = maxSuccessfulRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    internal static RunRecord CreateQuarantineSkippedRecord(
        ScriptInstance script, string mode, string queueId, string queueName,
        ResolvedScriptUser user, string resource)
    {
        DateTime now = DateTime.Now;
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
            ResultDetail = "未执行：等待恢复隔离解除",
            ResultCode = "run.not_started_quarantined",
            ResultArgs = new Dictionary<string, string>(StringComparer.Ordinal) { ["resource"] = resource },
            Outcomes = new RunOutcomeDimensions("not_started", "unverified", "not_started", "quarantined"),
        };
    }

    private RunRecord PersistRecord(
        RunningExecution exec,
        RunRecord record,
        List<string> attemptLogs,
        IReadOnlyList<RunScreenshot> screenshots,
        string displayName,
        bool requireDurable = false)
    {
        _plugins?.EnrichHistory(record);
        HistorySaveResult result;
        try
        {
            if (_history is ITaskHistoryCheckpoints checkpoints)
            {
                try { checkpoints.SaveTaskCheckpoint(record); }
                catch (Exception ex) { exec.SetPersistenceWarning("Task checkpoint: " + ex.GetType().Name); }
            }
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
            if (requireDurable)
                throw new IOException("受阻项历史未确认持久化，已停止后继扫描：" + result.PersistenceWarning);
        }
        // RunHistoryService 返回的是提交后的快照；通知文本属于运行时字段，需要随最终快照保留。
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
            ResultDetail = reason,
            ResultCode = "run.host_error",
            AttemptDetails = new List<RunAttempt>
            {
                new()
                {
                    Number = 1,
                    StartTime = now,
                    EndTime = now,
                    Status = "failed",
                    Reason = reason,
                    ReasonCode = "run.host_error",
                },
            },
        };
    }

    private static RunRecord CreateDependencySkippedRecord(
        ScriptInstance? script, QueueTask task, string mode, string queueId, string queueName,
        IReadOnlyList<string> blockedDependencies, bool quarantined)
    {
        DateTime now = DateTime.Now;
        return new RunRecord
        {
            ScriptInstanceId = task.ScriptInstanceId,
            ScriptName = script?.Name ?? "(脚本实例不存在)",
            QueueId = queueId,
            QueueName = queueName,
            Mode = mode,
            StartTime = now,
            EndTime = now,
            Attempts = 0,
            Status = "skipped",
            ResultDetail = "未执行：前置队列任务未确认成功",
            ResultCode = "run.not_started_dependency",
            ResultArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                { ["dependencyTaskIds"] = string.Join(",", blockedDependencies) },
            Outcomes = new RunOutcomeDimensions("not_started", "unverified", "not_started",
                quarantined ? "quarantined" : "not_required"),
        };
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
        if (!exec.BeginLogSegment(Guid.NewGuid().ToString("N")))
        {
            exec.Status = "cancelled";
            return new List<RunRecord>();
        }
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
            records.Add(PublishUnavailableRecord(exec, script, queueId, queueName, null, detail, unavailableReason));
            return records;
        }

        foreach (ResolvedScriptUser user in users)
        {
            records.Add(PublishUnavailableRecord(exec, script, queueId, queueName, user, detail, unavailableReason));
        }
        return records;
    }

    private RunRecord PublishUnavailableRecord(
        RunningExecution exec,
        ScriptInstance script,
        string queueId,
        string queueName,
        ResolvedScriptUser? user,
        string detail,
        string unavailableReason)
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
            ResultDetail = detail,
            ResultCode = "run.plugin_unavailable",
            ResultArgs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = unavailableReason,
            },
            AttemptDetails = new List<RunAttempt>
            {
                new()
                {
                    Number = 1,
                    StartTime = now,
                    EndTime = now,
                    Status = "failed",
                    Reason = detail,
                    ReasonCode = "run.plugin_unavailable",
                    ReasonArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reason"] = unavailableReason,
                    },
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
