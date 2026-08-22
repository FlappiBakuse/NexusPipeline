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

    public ExecutionPlanBuilder(
        IScriptRepository scripts,
        IQueueRepository queues,
        IUserRepository users,
        ExecutionValidator validator)
    {
        _scripts = scripts;
        _queues = queues;
        _users = users;
        _validator = validator;
    }

    public ScriptExecutionPlan BuildScript(string scriptId, string? userName)
    {
        ScriptInstance? source = _scripts.Snapshot().FirstOrDefault(item => item.Id == scriptId);
        if (source is null)
        {
            throw new InvalidOperationException($"脚本实例不存在：{scriptId}");
        }
        ScriptInstance script = source.Clone();

        _validator.ValidateScriptStart(script, userName);
        List<string> users = string.IsNullOrWhiteSpace(userName)
            ? _users.EnabledNames(script).ToList()
            : new List<string> { userName };
        return new ScriptExecutionPlan(
            script,
            users,
            ExecutionAdmissionProfile.ForScript(script),
            string.IsNullOrWhiteSpace(userName) ? Math.Max(1, users.Count) : 1);
    }

    public QueueExecutionPlan BuildQueue(string queueId)
    {
        List<ScriptInstance> scripts = _scripts.Snapshot().Select(script => script.Clone()).ToList();
        DispatchQueue? source = _queues.Snapshot().FirstOrDefault(item => item.Id == queueId);
        if (source is null)
        {
            throw new InvalidOperationException($"调度队列不存在：{queueId}");
        }
        DispatchQueue queue = source.Clone();

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

        PlannedQueueTask? blocked = tasks.FirstOrDefault(task =>
            task.Script is not null && ExecutionValidator.IsScriptRunning(task.Script));
        if (blocked?.Script is not null)
        {
            throw new InvalidOperationException(
                $"队列「{queue.Name}」引用的脚本「{blocked.Script.Name}」正在运行，请先退出后再执行");
        }

        DispatchQueue queueSnapshot = queue.Clone();
        queueSnapshot.Tasks = tasks.Select(task => CloneTask(task.Task)).ToList();
        ExecutionAdmissionProfile admission = ExecutionAdmissionProfile.ForQueue(queueSnapshot, tasks);
        int totalTasks = tasks.Sum(task => task.Script is null ? 1 : task.EnabledUsers.Count);
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
