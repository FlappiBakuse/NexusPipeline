using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 构建与真实执行共用的冻结计划，并以只读方式评估当前准入状态。
/// 该服务不登记运行、不创建租约、不写历史，也不触发调度或系统操作。
/// </summary>
internal sealed class ExecutionExplainService
{
    private readonly ExecutionPlanBuilder _plans;
    private readonly ExecutionStateStore _state;
    private readonly IHistoryStore _history;
    private readonly IPluginAvailability _availability;

    public ExecutionExplainService(
        ExecutionPlanBuilder plans,
        ExecutionStateStore state,
        IHistoryStore history,
        IPluginAvailability availability)
    {
        _plans = plans;
        _state = state;
        _history = history;
        _availability = availability;
    }

    public ExecutionExplainResult ExplainScript(string scriptId, string? userName = null)
    {
        ScriptExecutionPlan plan = _plans.BuildScriptForExplain(scriptId, userName);
        List<string> warnings = new();
        ExecutionAdmissionFailure? failure = null;
        if (ExecutionValidator.IsScriptRunning(plan.Script))
        {
            failure = new ExecutionAdmissionFailure(
                ExecutionAdmissionFailureCode.ProcessConflict,
                $"脚本「{plan.Script.Name}」正在运行，请先退出后再执行");
        }
        else
        {
            failure = _state.EvaluateCandidate(
                "script",
                plan.Script.Id,
                plan.Script.Name,
                plan.Admission);
        }

        AddScriptWarnings(plan.Script, plan.ResolvedSpec, warnings);
        var historyCache = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        List<ExecutionExplainUser> users = BuildUsers(
            plan.Script,
            plan.ResolvedUsers,
            userName,
            historyCache,
            warnings);
        List<ExecutionExplainTask> tasks = new()
        {
            new ExecutionExplainTask(
                plan.Script.Id,
                0,
                plan.Script.Id,
                plan.Script.Name,
                users.Count,
                NullIfEmpty(plan.Script.ConfigPath),
                NullIfEmpty(plan.Script.LogPath)),
        };
        return BuildResult(
            "script",
            plan.Script.Id,
            plan.Script.Name,
            plan.TotalTasks,
            plan.Admission,
            failure,
            users,
            tasks,
            warnings);
    }

    public ExecutionExplainResult ExplainQueue(string queueId)
    {
        QueueExecutionPlan plan = _plans.BuildQueueForExplain(queueId);
        List<string> warnings = new();
        ExecutionAdmissionFailure? failure = null;
        PlannedQueueTask? blocked = plan.Tasks.FirstOrDefault(task =>
            task.Script is not null && ExecutionValidator.IsScriptRunning(task.Script));
        if (blocked?.Script is not null)
        {
            failure = new ExecutionAdmissionFailure(
                ExecutionAdmissionFailureCode.ProcessConflict,
                $"队列「{plan.Queue.Name}」引用的脚本「{blocked.Script.Name}」正在运行，请先退出后再执行");
        }
        else
        {
            failure = _state.EvaluateCandidate(
                "queue",
                plan.Queue.Id,
                plan.Queue.Name,
                plan.Admission);
        }

        var historyCache = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        var users = new List<ExecutionExplainUser>();
        var tasks = new List<ExecutionExplainTask>();
        foreach (PlannedQueueTask task in plan.Tasks.OrderBy(item => item.Task.Index))
        {
            ScriptInstance? script = task.Script;
            if (script is null)
            {
                warnings.Add($"任务 {task.Task.Id} 引用的脚本实例不存在：{task.Task.ScriptInstanceId}");
                tasks.Add(new ExecutionExplainTask(
                    task.Task.Id,
                    task.Task.Index,
                    task.Task.ScriptInstanceId,
                    "（脚本实例不存在）",
                    0,
                    null,
                    null));
                continue;
            }

            AddScriptWarnings(script, task.ResolvedSpec, warnings);
            List<ExecutionExplainUser> taskUsers = BuildUsers(
                script,
                task.ResolvedUsers,
                requestedUserName: null,
                historyCache,
                warnings);
            users.AddRange(taskUsers);
            if (taskUsers.Count == 0)
            {
                warnings.Add($"脚本「{script.Name}」未配置启用用户，运行时将生成跳过记录");
            }
            tasks.Add(new ExecutionExplainTask(
                task.Task.Id,
                task.Task.Index,
                script.Id,
                script.Name,
                taskUsers.Count,
                NullIfEmpty(script.ConfigPath),
                NullIfEmpty(script.LogPath)));
        }

        return BuildResult(
            "queue",
            plan.Queue.Id,
            plan.Queue.Name,
            plan.TotalTasks,
            plan.Admission,
            failure,
            users,
            tasks,
            warnings);
    }

    private List<ExecutionExplainUser> BuildUsers(
        ScriptInstance script,
        IReadOnlyList<ResolvedScriptUser>? resolvedUsers,
        string? requestedUserName,
        IDictionary<string, IReadOnlyDictionary<string, int>> historyCache,
        ICollection<string> warnings)
    {
        List<ResolvedScriptUser> source = resolvedUsers?.ToList() ?? new List<ResolvedScriptUser>();
        if (source.Count == 0 && !string.IsNullOrWhiteSpace(requestedUserName))
        {
            string? unavailable = PluginAvailability.GetUnavailableReason(script, _availability);
            if (unavailable is not null)
            {
                return new List<ExecutionExplainUser>
                {
                    new(
                        script.Id,
                        "",
                        requestedUserName.Trim(),
                        "skipped",
                        "plugin_unavailable",
                        unavailable,
                        null,
                        -1),
                };
            }
        }

        string? pluginUnavailable = PluginAvailability.GetUnavailableReason(script, _availability);
        IReadOnlyDictionary<string, int>? counts = null;
        if (pluginUnavailable is null)
        {
            if (!historyCache.TryGetValue(script.Id, out counts))
            {
                try
                {
                    counts = _history.GetSuccessfulRunsByUser(DateTime.Today, script.Id);
                    historyCache[script.Id] = counts;
                }
                catch (Exception ex)
                {
                    warnings.Add($"无法读取脚本「{script.Name}」的今日运行历史：{ex.Message}");
                    counts = null;
                }
            }
        }

        var result = new List<ExecutionExplainUser>(source.Count);
        foreach (ResolvedScriptUser user in source)
        {
            string status = "ready";
            string reasonCode = "ready";
            string reason = "符合当前用户绑定和每日运行限制";
            int? successfulRuns = null;
            int max = user.Binding.MaxSuccessfulRunsPerDay;
            if (pluginUnavailable is not null)
            {
                status = "skipped";
                reasonCode = "plugin_unavailable";
                reason = pluginUnavailable;
            }
            else if (user.Spec is { Succeeded: false } failedSpec)
            {
                status = "blocked";
                reasonCode = "invalid_script_spec";
                reason = failedSpec.Error ?? "脚本专项配置解析失败";
            }
            else if (counts is null && max > 0)
            {
                status = "blocked";
                reasonCode = "history_unavailable";
                reason = "无法读取今日成功运行次数，暂不能确认每日限制";
            }
            else if (counts is not null)
            {
                counts.TryGetValue(user.UserId, out int count);
                successfulRuns = count;
                if (ExecutionUserEligibility.HasReachedDailySuccessCap(user, count))
                {
                    status = "skipped";
                    reasonCode = "daily_success_cap";
                    reason = ExecutionUserEligibility.DailySuccessCapReason(count, max);
                }
            }

            result.Add(new ExecutionExplainUser(
                script.Id,
                user.UserId,
                user.UserName,
                status,
                reasonCode,
                reason,
                successfulRuns,
                max));
        }
        return result;
    }

    private static void AddScriptWarnings(
        ScriptInstance script,
        ResolvedScriptSpec? spec,
        ICollection<string> warnings)
    {
        if (spec is { Succeeded: false } && !string.IsNullOrWhiteSpace(spec.Error))
        {
            warnings.Add(spec.Error);
        }
        if (string.IsNullOrWhiteSpace(script.ConfigPath))
        {
            warnings.Add($"脚本「{script.Name}」未声明配置路径");
        }
    }

    private static ExecutionExplainResult BuildResult(
        string kind,
        string targetId,
        string targetName,
        int totalTasks,
        ExecutionAdmissionProfile admission,
        ExecutionAdmissionFailure? failure,
        IReadOnlyList<ExecutionExplainUser> users,
        IReadOnlyList<ExecutionExplainTask> tasks,
        IReadOnlyList<string> warnings)
    {
        return new ExecutionExplainResult(
            kind,
            targetId,
            targetName,
            DateTimeOffset.Now,
            failure is null,
            totalTasks,
            admission.QueueClass?.ToString().ToLowerInvariant() ?? "",
            ExecutionAdmissionProfile.NormalizeCompletionAction(admission.CompletionAction),
            failure is null
                ? null
                : new ExecutionExplainAdmissionFailure(
                    failure.StableCode,
                    failure.Message,
                    failure.Disposition.ToString().ToLowerInvariant(),
                    failure.ConflictingRunId,
                    failure.Resource),
            ToResources(admission.Resources),
            users,
            tasks,
            warnings.Distinct(StringComparer.Ordinal).ToList());
    }

    private static ExecutionExplainResources ToResources(ExecutionResourceSet resources)
    {
        return new ExecutionExplainResources(
            resources.ScriptIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            resources.UserDataKeys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            resources.ExecutablePaths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            resources.ProcessNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            resources.ConfigPaths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            resources.EmulatorEndpoints.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            resources.LogResources.Select(resource => resource.DisplayPath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            resources.AuxiliaryExecutablePaths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            resources.AuxiliaryProcessNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
