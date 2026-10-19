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

/// <summary>
/// 执行生命周期运行器：承载脚本/队列后台任务、用户串行执行、历史落盘和完成通知。
/// 队列之间的并行准入与完成意图由 ExecutionStateStore/SystemActionExecutor 统一协调。
/// </summary>
internal sealed partial class ExecutionRunner
{
    private readonly IUserRepository _users;
    private readonly IHistoryStore _history;
    private readonly INotificationService _notifications;
    private readonly SystemActionExecutor _systemActions;
    private readonly IPluginAvailability _pluginAvailability;
    private readonly IEmulatorSupportProviderResolver _emulatorSupportProviders;
    private readonly IUserRunStartingPublisher? _userRunEvents;
    private readonly PluginManager? _plugins;
    private readonly OutboundHttpClientProvider? _http;
    private readonly ExecutionStateStore? _state;

    public ExecutionRunner(
        IUserRepository users,
        IHistoryStore history,
        INotificationService notifications,
        SystemActionExecutor systemActions,
        IPluginAvailability pluginAvailability,
        IEmulatorSupportProviderResolver emulatorSupportProviders,
        IUserRunStartingPublisher? userRunEvents = null,
        PluginManager? plugins = null,
        OutboundHttpClientProvider? http = null,
        ExecutionStateStore? state = null)
    {
        _users = users;
        _history = history;
        _notifications = notifications;
        _systemActions = systemActions;
        _pluginAvailability = pluginAvailability ?? throw new ArgumentNullException(nameof(pluginAvailability));
        _emulatorSupportProviders = emulatorSupportProviders ?? throw new ArgumentNullException(nameof(emulatorSupportProviders));
        _userRunEvents = userRunEvents;
        _plugins = plugins;
        _http = http;
        _state = state;
    }

    public async Task RunScriptAsync(RunningExecution exec, ScriptExecutionPlan plan)
    {
        ScriptInstance script = plan.Script;
        try
        {
            exec.SetPreviewWaiting(script);
            string? unavailableReason = PluginAvailability.GetUnavailableReason(
                script,
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

    public async Task RunQueueAsync(RunningExecution exec, QueueExecutionPlan plan)
    {
        DispatchQueue queue = plan.Queue;
        var records = new List<RunRecord>();
        CompletionIntent? completionIntent = null;
        try
        {
            var taskOutcomes = new Dictionary<string, string>(StringComparer.Ordinal);
            List<PlannedQueueTask> tasks = plan.Tasks.ToList();
            for (int i = 0; i < tasks.Count; i++)
            {
                if (exec.Cts.IsCancellationRequested)
                {
                    exec.Status = "cancelled";
                    Logger.Info($"调度队列「{queue.Name}」已被取消，后续任务不再执行。");
                    break;
                }

                // Publish an authoritative empty tail before exposing the next queue item.
                if (!exec.BeginLogSegment($"queue:{i + 1}"))
                {
                    exec.Status = "cancelled";
                    break;
                }

                PlannedQueueTask planned = tasks[i];
                QueueTask task = planned.Task;
                ScriptInstance? script = planned.Script;
                string[] blockedDependencies = (task.DependsOnTaskIds ?? [])
                    .Where(id => !taskOutcomes.TryGetValue(id, out string? status) || status != "success")
                    .ToArray();
                if (blockedDependencies.Length > 0)
                {
                    bool quarantined = blockedDependencies.Any(id =>
                        taskOutcomes.GetValueOrDefault(id) == "quarantined");
                    var dependentUsers = ResolvePlanUsers(planned.ResolvedUsers);
                    IEnumerable<ResolvedScriptUser?> recipients = dependentUsers.Count == 0
                        ? new ResolvedScriptUser?[] { null } : dependentUsers;
                    foreach (ResolvedScriptUser? user in recipients)
                    {
                        RunRecord dependent = CreateDependencySkippedRecord(script, task, exec.Mode,
                            queue.Id, queue.Name, blockedDependencies, quarantined);
                        dependent.UserId = user?.UserId ?? "";
                        dependent.UserName = user?.UserName ?? "";
                        RunRecord published = PersistRecord(exec, dependent, new List<string>(),
                            Array.Empty<RunScreenshot>(), script?.Name ?? queue.Name, requireDurable: true);
                        records.Add(published);
                        exec.AddRecordAndIncrement(published);
                        if (!queue.NotifyEnabled && script is not null && user is not null)
                            await NotifyScriptIfEnabledAsync(script, published, user.Binding, user.UserName).ConfigureAwait(false);
                    }
                    taskOutcomes[task.Id] = quarantined ? "quarantined" : "blocked";
                    exec.CurrentStatus = "未执行（前置任务未确认成功）";
                    continue;
                }
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
                        ResultDetail = "脚本实例不存在或已被删除",
                        ResultCode = "run.script_missing",
                    };
                    RunRecord publishedMissing = PersistRecord(exec, missing, new List<string>(), Array.Empty<RunScreenshot>(), queue.Name);
                    records.Add(publishedMissing);
                    taskOutcomes[task.Id] = "blocked";
                    exec.AddRecordAndIncrement(publishedMissing);
                    Logger.Warn($"[警告] 调度队列「{queue.Name}」第 {i + 1} 项引用的脚本实例不存在，已跳过。");
                    continue;
                }

                exec.CurrentScriptName = script.Name;
                exec.SetPreviewWaiting(script);
                exec.CurrentAttempt = 0;
                exec.CurrentStatus = "等待开始";
                string? unavailableReason = PluginAvailability.GetUnavailableReason(
                    script,
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
                    taskOutcomes[task.Id] = "blocked";
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
                        Status = "skipped",
                        ResultDetail = "脚本实例没有启用的账号绑定，已跳过",
                        ResultCode = "run.no_enabled_users",
                    };
                    RunRecord publishedSkipped = PersistRecord(exec, skipped, new List<string>(), Array.Empty<RunScreenshot>(), queue.Name);
                    records.Add(publishedSkipped);
                    taskOutcomes[task.Id] = "blocked";
                    exec.AddRecordAndIncrement(publishedSkipped);
                    Logger.Warn($"[警告] 调度队列「{queue.Name}」第 {i + 1} 项引用的脚本实例「{script.Name}」未配置启用用户，已跳过。");
                    continue;
                }
                List<RunRecord> itemRecords = await RunUsersAsync(
                    exec,
                    script,
                    queue.Id,
                    queue.Name,
                    runUsers,
                    planned.ResolvedSpec,
                    userIndex => userIndex < runUsers.Count - 1 || i < tasks.Count - 1 ? "yes" : "no",
                    userIndex =>
                    {
                        ScriptInstance? next = userIndex < runUsers.Count - 1
                            ? script
                            : i < tasks.Count - 1 ? tasks[i + 1].Script : null;
                        if (next is null) return ("unknown", "unknown");
                        string relation = string.IsNullOrWhiteSpace(script.GameExe)
                            || string.IsNullOrWhiteSpace(next.GameExe)
                            ? "unknown"
                            : string.Equals(script.GameExe.Replace('/', '\\'), next.GameExe.Replace('/', '\\'),
                                StringComparison.OrdinalIgnoreCase) ? "same" : "different";
                        ResolvedScriptSpec? nextSpec = userIndex < runUsers.Count - 1
                            ? planned.ResolvedSpec
                            : i < tasks.Count - 1 ? tasks[i + 1].ResolvedSpec : null;
                        string owner = !next.LaunchGame ? "already_running"
                            : ExecutionCoordinator.ShouldHostLaunchGame(next, nextSpec) ? "host" : "upstream";
                        return (relation, owner);
                    }, suppressNotifications: queue.NotifyEnabled).ConfigureAwait(false);
                records.AddRange(itemRecords);
                taskOutcomes[task.Id] = itemRecords.Any(record => record.ResultCode == "run.not_started_quarantined")
                    ? "quarantined"
                    : itemRecords.Count > 0 && itemRecords.All(record => record.Status == "success")
                        ? "success" : "blocked";
                if (exec.Status == "cancelled")
                {
                    break;
                }
            }
            bool anyCancelled = exec.Status == "cancelled"
                || exec.Cts.IsCancellationRequested
                || records.Any(record => record.Status == "cancelled");
            exec.Status = anyCancelled ? "cancelled" : "done";
            if (!anyCancelled && !records.Any(record => record.ResultCode is "run.not_started_quarantined" or "run.not_started_dependency"))
            {
                Logger.Info($"调度队列「{queue.Name}」全部任务执行完毕，执行完成操作：{QueueRule.CompletionActionDesc(queue.CompletionAction)}。");
                completionIntent = new CompletionIntent(exec.Id, queue.Name, queue.CompletionAction);
            }
            else
            {
                Logger.Warn($"调度队列「{queue.Name}」存在取消或恢复隔离未执行项，跳过完成操作。");
            }
        }
        catch (Exception ex)
        {
            exec.Status = "error";
            Logger.Error($"[错误] 调度队列「{queue.Name}」运行异常：{ex}");
        }
        finally
        {
            if (queue.NotifyEnabled)
            {
                try { await _notifications.NotifyQueueAsync(queue, records).ConfigureAwait(false); }
                catch { Logger.Warn("[通知] 队列汇总发送失败，执行结果保持不变。"); }
            }
            exec.ClearPreviewTarget();
            exec.FinishedAt = DateTime.Now;
            _systemActions.CompleteExecution(exec, completionIntent);
        }
    }
}
