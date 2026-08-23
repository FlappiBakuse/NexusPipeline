using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;

namespace NexusPipeline.Services.Execution;

/// <summary>队列任务的冻结快照：脚本引用解析结果与启用用户顺序均在准入前固定。</summary>
internal sealed record PlannedQueueTask(
    QueueTask Task,
    ScriptInstance? Script,
    IReadOnlyList<string> EnabledUsers);

/// <summary>脚本运行计划，运行期间不再回读共享仓储。</summary>
internal sealed record ScriptExecutionPlan(
    ScriptInstance Script,
    IReadOnlyList<string> Users,
    ExecutionAdmissionProfile Admission,
    int TotalTasks);

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

    public ExecutionPlanBuilder(
        IScriptRepository scripts,
        IQueueRepository queues,
        IUserRepository users,
        ExecutionValidator validator,
        IExecutionSnapshotProvider? snapshots = null,
        IPluginCapabilityResolver? capabilities = null)
    {
        _scripts = scripts;
        _queues = queues;
        _users = users;
        _validator = validator;
        _snapshots = snapshots;
        _capabilities = capabilities;
    }

    public ScriptExecutionPlan BuildScript(string scriptId, string? userName)
    {
        ScriptInstance? script = _snapshots?.SnapshotScript(scriptId)?.Script
            ?? _scripts.Snapshot().FirstOrDefault(item => item.Id == scriptId)?.Clone();
        if (script is null)
        {
            throw new InvalidOperationException($"脚本实例不存在：{scriptId}");
        }

        _validator.ValidateScriptStart(script, userName);
        List<string> users = string.IsNullOrWhiteSpace(userName)
            ? _users.EnabledNames(script).ToList()
            : new List<string> { userName };
        return new ScriptExecutionPlan(
            script,
            users,
            ExecutionAdmissionProfile.ForScript(script, userName, _capabilities),
            string.IsNullOrWhiteSpace(userName) ? Math.Max(1, users.Count) : 1);
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
                item.EnabledUsers.ToList()))
            .ToList();
        ExecutionAdmissionProfile admission = data.Admission is null
            ? ExecutionAdmissionProfile.ForQueue(queue, tasks, _capabilities)
            : RestoreAdmission(data.Admission);
        int totalTasks = tasks.Sum(task => task.Script is null || task.EnabledUsers.Count == 0
            ? 1
            : task.EnabledUsers.Count);
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

        _validator.ValidateQueueStartSnapshot(queue, scripts);
        List<PlannedQueueTask> tasks = queue.Tasks
            .OrderBy(task => task.Index)
            .Select(task =>
            {
                ScriptInstance? script = scripts.FirstOrDefault(item => item.Id == task.ScriptInstanceId)?.Clone();
                return new PlannedQueueTask(
                    CloneTask(task),
                    script,
                    script is null ? Array.Empty<string>() : _users.EnabledNames(script).ToList());
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
        int totalTasks = tasks.Sum(task => task.Script is null || task.EnabledUsers.Count == 0
            ? 1
            : task.EnabledUsers.Count);
        return new QueueExecutionPlan(queueSnapshot, tasks, admission, totalTasks);
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
