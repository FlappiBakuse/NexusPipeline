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
        List<ExecutionExplainWarning> warnings = new();
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
        List<ExecutionExplainWarning> warnings = new();
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
                AddWarning(warnings, "script_missing", ("taskId", task.Task.Id), ("scriptId", task.Task.ScriptInstanceId));
                tasks.Add(new ExecutionExplainTask(
                    task.Task.Id,
                    task.Task.Index,
                    task.Task.ScriptInstanceId,
                    "",
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
                AddWarning(warnings, "no_enabled_users", ("scriptId", script.Id));
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
        ICollection<ExecutionExplainWarning> warnings)
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
                        Args(("scriptId", script.Id)),
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
                catch (Exception)
                {
                    AddWarning(warnings, "history_unavailable", ("scriptId", script.Id));
                    counts = null;
                }
            }
        }

        var result = new List<ExecutionExplainUser>(source.Count);
        foreach (ResolvedScriptUser user in source)
        {
            string status = "ready";
            string reasonCode = "ready";
            IReadOnlyDictionary<string, object?> reasonArgs = Args();
            int? successfulRuns = null;
            int max = user.Binding.MaxSuccessfulRunsPerDay;
            if (pluginUnavailable is not null)
            {
                status = "skipped";
                reasonCode = "plugin_unavailable";
                reasonArgs = Args(("scriptId", script.Id));
            }
            else if (user.Spec is { Succeeded: false } failedSpec)
            {
                status = "blocked";
                reasonCode = "invalid_script_spec";
                reasonArgs = Args(("scriptId", script.Id));
            }
            else if (counts is null && max > 0)
            {
                status = "blocked";
                reasonCode = "history_unavailable";
                reasonArgs = Args(("scriptId", script.Id));
            }
            else if (counts is not null)
            {
                counts.TryGetValue(user.UserId, out int count);
                successfulRuns = count;
                if (ExecutionUserEligibility.HasReachedDailySuccessCap(user, count))
                {
                    status = "skipped";
                    reasonCode = "daily_success_cap";
                    reasonArgs = Args(("successfulRuns", count), ("maximum", max));
                }
            }

            result.Add(new ExecutionExplainUser(
                script.Id,
                user.UserId,
                user.UserName,
                status,
                reasonCode,
                reasonArgs,
                successfulRuns,
                max));
        }
        return result;
    }

    private static void AddScriptWarnings(
        ScriptInstance script,
        ResolvedScriptSpec? spec,
        ICollection<ExecutionExplainWarning> warnings)
    {
        if (spec is { Succeeded: false } && !string.IsNullOrWhiteSpace(spec.Error))
        {
            AddWarning(warnings, "invalid_script_spec", ("scriptId", script.Id));
        }
        if (string.IsNullOrWhiteSpace(script.ConfigPath))
        {
            AddWarning(warnings, "config_path_missing", ("scriptId", script.Id));
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
        IReadOnlyList<ExecutionExplainWarning> warnings)
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
                    Args(("resource", failure.Resource)),
                    failure.Disposition.ToString().ToLowerInvariant(),
                    failure.ConflictingRunId,
                    failure.Resource),
            ToResources(admission.Resources),
            users,
            tasks,
            warnings
                .GroupBy(warning => warning.Code + "|" + string.Join(";", warning.Args.Select(item => $"{item.Key}={item.Value}")), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList());
    }

    private static void AddWarning(
        ICollection<ExecutionExplainWarning> warnings,
        string code,
        params (string Key, object? Value)[] args)
    {
        warnings.Add(new ExecutionExplainWarning(code, Args(args)));
    }

    private static IReadOnlyDictionary<string, object?> Args(
        params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
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
