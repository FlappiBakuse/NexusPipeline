using NexusPipeline.App.Abstractions;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
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
    private readonly IEmulatorCapabilityProvider _emulator;
    private readonly SystemActionExecutor _systemActions;

    public ExecutionRunner(
        IUserRepository users,
        IHistoryStore history,
        INotificationService notifications,
        IEmulatorCapabilityProvider emulator,
        SystemActionExecutor systemActions)
    {
        _users = users;
        _history = history;
        _notifications = notifications;
        _emulator = emulator;
        _systemActions = systemActions;
    }

    public async Task RunScriptAsync(RunningExecution exec, ScriptExecutionPlan plan)
    {
        ScriptInstance script = plan.Script;
        try
        {
            List<string?> runUsers = plan.Users.Cast<string?>().ToList();
            List<RunRecord> records = await RunUsersAsync(exec, script, "", "", runUsers).ConfigureAwait(false);
            if (records.Count > 0 && records[^1].Status == "cancelled")
            {
                exec.Status = "cancelled";
            }
            else if (exec.Status != "cancelled")
            {
                exec.Status = "done";
            }
            if (script.NotifyEnabled)
            {
                foreach (RunRecord record in records)
                {
                    await _notifications.NotifyScriptAsync(script, record).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            exec.Status = "error";
            Logger.Error($"[错误] 脚本「{script.Name}」运行异常：{ex}");
        }
        finally
        {
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
        List<string?> users)
    {
        var records = new List<RunRecord>();
        foreach (string? runUser in users)
        {
            if (exec.Cts.IsCancellationRequested)
            {
                exec.Status = "cancelled";
                break;
            }
            string displayName = runUser is null ? script.Name : $"{script.Name}（{runUser}）";
            exec.CurrentScriptName = displayName;
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
            try
            {
                var session = new ExecutionCoordinator(
                    script, exec.Mode, queueId, queueName, runUser,
                    exec.Cts.Token,
                    (attempt, max) =>
                    {
                        exec.CurrentAttempt = attempt;
                        exec.CurrentMaxAttempts = max;
                    },
                    status => exec.CurrentStatus = status,
                    line => exec.AppendLog(line),
                    _users,
                    _emulator);

                RunRecord record = await session.RunAsync().ConfigureAwait(false);
                records.Add(record);
                exec.AddRecordAndIncrement(record);
                exec.CurrentStatus = record.Status == "success" ? "运行成功" : (record.Status == "cancelled" ? "已取消" : "运行失败");
                Logger.Info($"[{(exec.Mode == "auto" ? "自动" : "手动")}运行] 脚本「{displayName}」最终结果：{record.Status}（{record.ResultDetail}）");
                _history.Save(record, session.AttemptLogs);
            }
            finally
            {
                gate.Release();
            }
        }
        return records;
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
                        ResultDetail = "脚本实例不存在或已被删除",
                    };
                    records.Add(missing);
                    exec.AddRecordAndIncrement(missing);
                    _history.Save(missing, new List<string>());
                    Logger.Warn($"[警告] 调度队列「{queue.Name}」第 {i + 1} 项引用的脚本实例不存在，已跳过。");
                    continue;
                }

                exec.CurrentScriptName = script.Name;
                exec.CurrentAttempt = 0;
                exec.CurrentStatus = "等待开始";
                List<string?> runUsers = planned.EnabledUsers.Cast<string?>().ToList();
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
                        ResultDetail = "脚本实例未配置启用用户，已跳过",
                    };
                    records.Add(skipped);
                    exec.AddRecordAndIncrement(skipped);
                    _history.Save(skipped, new List<string>());
                    Logger.Warn($"[警告] 调度队列「{queue.Name}」第 {i + 1} 项引用的脚本实例「{script.Name}」未配置启用用户，已跳过。");
                    continue;
                }
                records.AddRange(await RunUsersAsync(exec, script, queue.Id, queue.Name, runUsers).ConfigureAwait(false));
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
            else
            {
                foreach (RunRecord record in records)
                {
                    ScriptInstance? script = tasks
                        .FirstOrDefault(item => item.Task.ScriptInstanceId == record.ScriptInstanceId)
                        ?.Script;
                    if (script is not null && script.NotifyEnabled)
                    {
                        await _notifications.NotifyScriptAsync(script, record).ConfigureAwait(false);
                    }
                }
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
            exec.FinishedAt = DateTime.Now;
            _systemActions.CompleteExecution(exec, completionIntent);
        }
    }
}
