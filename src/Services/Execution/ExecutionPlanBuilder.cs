using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;

namespace NexusPipeline.Services.Execution;

/// <summary>队列任务的冻结快照：脚本引用解析结果与启用用户顺序均在准入前固定。</summary>
internal sealed record PlannedQueueTask(
    QueueTask Task,
    ScriptInstance? Script,
    IReadOnlyList<string> EnabledUsers,
    IReadOnlyList<ResolvedScriptUser>? ResolvedUsers = null,
    ResolvedScriptSpec? ResolvedSpec = null);

/// <summary>脚本运行计划，运行期间不再回读共享仓储。</summary>
internal sealed record ScriptExecutionPlan(
    ScriptInstance Script,
    IReadOnlyList<string> Users,
    ExecutionAdmissionProfile Admission,
    int TotalTasks,
    IReadOnlyList<ResolvedScriptUser>? ResolvedUsers = null,
    ResolvedScriptSpec? ResolvedSpec = null);

/// <summary>队列运行计划，包含队列、任务、脚本和准入 profile 的同一时刻快照。</summary>
internal sealed record QueueExecutionPlan(
    DispatchQueue Queue,
    IReadOnlyList<PlannedQueueTask> Tasks,
    ExecutionAdmissionProfile Admission,
    int TotalTasks);

/// <summary>
/// 从仓储深拷贝快照构建执行计划。计划构建完成后，用户修改队列/脚本不会改变本次运行的分类、资源或执行顺序。
/// </summary>
internal sealed class ExecutionPlanBuilder
{
    private readonly IScriptRepository _scripts;
    private readonly IQueueRepository _queues;
    private readonly IUserRepository _users;
    private readonly ExecutionValidator _validator;
    private readonly IExecutionSnapshotProvider? _snapshots;
    private readonly IPluginCapabilityResolver? _capabilities;
    private readonly ScriptSpecResolver? _specs;
    private readonly IPluginAvailability? _availability;

    public ExecutionPlanBuilder(
        IScriptRepository scripts,
        IQueueRepository queues,
        IUserRepository users,
        ExecutionValidator validator,
        IExecutionSnapshotProvider? snapshots = null,
        IPluginCapabilityResolver? capabilities = null,
        ScriptSpecResolver? specs = null,
        IPluginAvailability? availability = null)
    {
        _scripts = scripts;
        _queues = queues;
        _users = users;
        _validator = validator;
        _snapshots = snapshots;
        _capabilities = capabilities;
        _specs = specs;
        _availability = availability;
    }

    public ScriptExecutionPlan BuildScript(string scriptId, string? userName)
    {
        ExecutionScriptSnapshot? executionSnapshot = _snapshots?.SnapshotScript(scriptId);
        ScriptInstance? script = executionSnapshot?.Script
            ?? _scripts.Snapshot().FirstOrDefault(item => item.Id == scriptId)?.Clone();
        if (script is null)
        {
            throw new InvalidOperationException($"脚本实例不存在：{scriptId}");
        }

        ResolvedScriptSpec? resolvedSpec = ResolveForExecution(script);
        if (resolvedSpec is not null)
        {
            script = resolvedSpec.Script;
        }

        _validator.ValidateScriptStart(script, userName);
        IReadOnlyList<ResolvedScriptUser> resolvedUsers = _users.ResolveEnabledBindings(script, executionSnapshot?.Users);
        ResolvedScriptUser? resolvedSingle = string.IsNullOrWhiteSpace(userName)
            ? null
            : _users.ResolveEnabledBinding(script, userName, executionSnapshot?.Users);
        if (!string.IsNullOrWhiteSpace(userName))
        {
            resolvedUsers = resolvedSingle is null
                ? Array.Empty<ResolvedScriptUser>()
                : new[] { resolvedSingle };
        }
        List<string> users = string.IsNullOrWhiteSpace(userName)
            ? resolvedUsers.Select(item => item.UserName).ToList()
            : new List<string> { resolvedSingle?.UserName ?? userName };
        return new ScriptExecutionPlan(
            script,
            users,
            ExecutionAdmissionProfile.ForScript(script, userName, _capabilities, resolvedUsers),
            string.IsNullOrWhiteSpace(userName) ? Math.Max(1, users.Count) : 1,
            resolvedUsers,
            resolvedSpec);
    }

    public QueueExecutionPlan BuildQueue(string queueId)
    {
        return BuildQueueInternal(queueId, checkProcessConflicts: true);
    }

    /// <summary>
    /// 为定时 occurrence 构建冻结计划。触发时只做计划/资源快照，进程冲突留到 Admission 重试，
    /// 这样“已触发但暂不能运行”的 occurrence 仍然拥有完整、可持久化的执行计划。
    /// </summary>
    public QueueExecutionPlan BuildQueueForSchedule(string queueId)
    {
        return BuildQueueInternal(queueId, checkProcessConflicts: false);
    }

    internal QueueExecutionPlan RestoreFrozenQueue(FrozenQueuePlanData data)
    {
        DispatchQueue queue = data.Queue.Clone();
        List<PlannedQueueTask> tasks = data.Tasks
            .Select(item => new PlannedQueueTask(
                CloneTask(item.Task),
                item.Script?.Clone(),
                item.EnabledUsers.ToList(),
                item.ResolvedUsers.Select(user => new ResolvedScriptUser(
                    user.UserId,
                    user.UserName,
                    user.Binding.Clone())).ToList(),
                item.Script is null || item.ResolvedSpec is null
                    ? null
                    : item.ResolvedSpec.ToRuntime(item.Script.Clone())))
            .ToList();
        ExecutionAdmissionProfile admission = data.Admission is null
            ? ExecutionAdmissionProfile.ForQueue(queue, tasks, _capabilities)
            : RestoreAdmission(data.Admission);
        int totalTasks = tasks.Sum(task => task.Script is null || task.ResolvedUsers is null || task.ResolvedUsers.Count == 0
            ? 1
            : task.ResolvedUsers.Count);
        return new QueueExecutionPlan(queue, tasks, admission, totalTasks);
    }

    internal static FrozenQueuePlanData FreezeQueue(QueueExecutionPlan plan)
    {
        return new FrozenQueuePlanData
        {
            Queue = plan.Queue.Clone(),
            Tasks = plan.Tasks.Select(task => new FrozenQueueTaskData
            {
                Task = CloneTask(task.Task),
                Script = task.Script?.Clone(),
                EnabledUsers = task.EnabledUsers.ToList(),
                ResolvedUsers = task.ResolvedUsers?.Select(user => new FrozenResolvedUserData
                {
                    UserId = user.UserId,
                    UserName = user.UserName,
                    Binding = user.Binding.Clone(),
                }).ToList() ?? new List<FrozenResolvedUserData>(),
                ResolvedSpec = task.ResolvedSpec is null
                    ? null
                    : FrozenResolvedScriptSpecData.From(task.ResolvedSpec),
            }).ToList(),
            Admission = FreezeAdmission(plan.Admission),
        };
    }

    private static FrozenAdmissionProfileData FreezeAdmission(ExecutionAdmissionProfile profile)
    {
        return new FrozenAdmissionProfileData
        {
            Kind = profile.Kind,
            QueueClass = profile.QueueClass?.ToString(),
            CompletionAction = profile.CompletionAction,
            ScriptIds = profile.Resources.ScriptIds.ToList(),
            UserDataKeys = profile.Resources.UserDataKeys.ToList(),
            ExecutablePaths = profile.Resources.ExecutablePaths.ToList(),
            ProcessNames = profile.Resources.ProcessNames.ToList(),
            ConfigPaths = profile.Resources.ConfigPaths.ToList(),
            EmulatorEndpoints = profile.Resources.EmulatorEndpoints.ToList(),
            LogResources = profile.Resources.LogResources.Select(resource => new FrozenLogResourceData
            {
                BaseDirectory = resource.BaseDirectory,
                Pattern = resource.Pattern,
                IsExactFile = resource.IsExactFile,
                DisplayPath = resource.DisplayPath,
            }).ToList(),
            AuxiliaryExecutablePaths = profile.Resources.AuxiliaryExecutablePaths.ToList(),
            AuxiliaryProcessNames = profile.Resources.AuxiliaryProcessNames.ToList(),
        };
    }

    private static ExecutionAdmissionProfile RestoreAdmission(FrozenAdmissionProfileData data)
    {
        ExecutionResourceSet resources = new(
            new HashSet<string>(data.ScriptIds, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(data.ExecutablePaths, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(data.ProcessNames, StringComparer.OrdinalIgnoreCase),
            data.ConfigPaths.ToList(),
            new HashSet<string>(data.EmulatorEndpoints, StringComparer.OrdinalIgnoreCase))
        {
            UserDataKeys = new HashSet<string>(data.UserDataKeys, StringComparer.OrdinalIgnoreCase),
            LogResources = data.LogResources.Select(resource => new LogResourceDescriptor(
                resource.BaseDirectory,
                resource.Pattern,
                resource.IsExactFile,
                resource.DisplayPath)).ToList(),
            AuxiliaryExecutablePaths = new HashSet<string>(data.AuxiliaryExecutablePaths, StringComparer.OrdinalIgnoreCase),
            AuxiliaryProcessNames = new HashSet<string>(data.AuxiliaryProcessNames, StringComparer.OrdinalIgnoreCase),
        };
        ExecutionConcurrencyClass? queueClass = Enum.TryParse(
            data.QueueClass,
            ignoreCase: true,
            out ExecutionConcurrencyClass parsedClass)
            ? parsedClass
            : null;
        return new ExecutionAdmissionProfile(data.Kind, queueClass, resources, data.CompletionAction);
    }

    private QueueExecutionPlan BuildQueueInternal(string queueId, bool checkProcessConflicts)
    {
        ExecutionQueueSnapshot? executionSnapshot = _snapshots?.SnapshotQueue(queueId);
        List<ScriptInstance> scripts;
        DispatchQueue? source;
        if (executionSnapshot is not null)
        {
            source = executionSnapshot.Queue;
            scripts = executionSnapshot.Scripts.Select(script => script.Clone()).ToList();
        }
        else
        {
            scripts = _scripts.Snapshot().Select(script => script.Clone()).ToList();
            source = _queues.Snapshot().FirstOrDefault(item => item.Id == queueId)?.Clone();
        }
        if (source is null)
        {
            throw new InvalidOperationException($"调度队列不存在：{queueId}");
        }
        DispatchQueue queue = source;

        HashSet<string> queuedScriptIds = queue.Tasks
            .Select(task => task.ScriptInstanceId)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, ResolvedScriptSpec?> resolvedSpecs = new(StringComparer.Ordinal);
        var effectiveScripts = new List<ScriptInstance>(scripts.Count);
        foreach (ScriptInstance declaration in scripts)
        {
            if (!queuedScriptIds.Contains(declaration.Id))
            {
                // 队列准入只依赖实际引用的脚本；无关脚本 profile 损坏不应阻断本队列。
                effectiveScripts.Add(declaration);
                continue;
            }
            ResolvedScriptSpec? resolved = ResolveForExecution(declaration);
            effectiveScripts.Add(resolved?.Script ?? declaration);
            resolvedSpecs[declaration.Id] = resolved;
        }
        scripts = effectiveScripts;
        _validator.ValidateQueueStartSnapshot(queue, scripts);
        List<PlannedQueueTask> tasks = queue.Tasks
            .OrderBy(task => task.Index)
            .Select(task =>
            {
                ScriptInstance? script = scripts.FirstOrDefault(item => item.Id == task.ScriptInstanceId)?.Clone();
                IReadOnlyList<ResolvedScriptUser> resolvedUsers = script is null
                    ? Array.Empty<ResolvedScriptUser>()
                    : _users.ResolveEnabledBindings(script, executionSnapshot?.Users);
                resolvedSpecs.TryGetValue(task.ScriptInstanceId, out ResolvedScriptSpec? resolvedSpec);
                return new PlannedQueueTask(
                    CloneTask(task),
                    script,
                    resolvedUsers.Select(user => user.UserName).ToList(),
                    resolvedUsers,
                    resolvedSpec);
            })
            .ToList();

        PlannedQueueTask? blocked = checkProcessConflicts
            ? tasks.FirstOrDefault(task => task.Script is not null && ExecutionValidator.IsScriptRunning(task.Script))
            : null;
        if (blocked?.Script is not null)
        {
            throw new ExecutionAdmissionException(new ExecutionAdmissionFailure(
                ExecutionAdmissionFailureCode.ProcessConflict,
                $"队列「{queue.Name}」引用的脚本「{blocked.Script.Name}」正在运行，请先退出后再执行"));
        }

        DispatchQueue queueSnapshot = queue.Clone();
        queueSnapshot.Tasks = tasks.Select(task => CloneTask(task.Task)).ToList();
        ExecutionAdmissionProfile admission = ExecutionAdmissionProfile.ForQueue(queueSnapshot, tasks, _capabilities);
        int totalTasks = tasks.Sum(task => task.Script is null || task.ResolvedUsers is null || task.ResolvedUsers.Count == 0
            ? 1
            : task.ResolvedUsers.Count);
        return new QueueExecutionPlan(queueSnapshot, tasks, admission, totalTasks);
    }

    private ResolvedScriptSpec? ResolveForExecution(ScriptInstance declaration)
    {
        if (_specs is null)
        {
            return null;
        }

        ResolvedScriptSpec resolved = _specs.Resolve(declaration);
        if (!resolved.Succeeded)
        {
            // 插件缺失/禁用时保留声明交给既有 runner fallback，执行记录仍会明确报告插件不可用；
            // 当前插件已启用但 profile/用户判断脚本损坏时则立即阻止计划，避免空路径启动。
            bool unavailable = !string.IsNullOrWhiteSpace(declaration.PluginType)
                && _availability is not null
                && PluginAvailability.GetUnavailableReason(declaration.PluginType, _availability) is not null;
            if (!unavailable)
            {
                throw new InvalidOperationException(resolved.Error);
            }
        }
        return resolved;
    }

    private static QueueTask CloneTask(QueueTask task)
    {
        return new QueueTask
        {
            Id = task.Id,
            Index = task.Index,
            ScriptInstanceId = task.ScriptInstanceId,
        };
    }

}
