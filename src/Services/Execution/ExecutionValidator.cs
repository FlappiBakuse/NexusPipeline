using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 执行门禁与运行前校验。
///
/// 该组件只负责把脚本/队列读取、用户规则、进程冲突和限制检查组合成明确的执行前置条件；
/// 不创建运行状态，也不启动后台任务，避免 DispatchCenter 同时承担验证和生命周期管理。
/// </summary>
internal sealed class ExecutionValidator
{
    private readonly IScriptRepository _scripts;
    private readonly IQueueRepository _queues;
    private readonly IUserRepository _users;
    private readonly IPluginAvailability _pluginAvailability;

    public ExecutionValidator(
        IScriptRepository scripts,
        IQueueRepository queues,
        IUserRepository users,
        IPluginAvailability pluginAvailability)
    {
        _scripts = scripts;
        _queues = queues;
        _users = users;
        _pluginAvailability = pluginAvailability ?? throw new ArgumentNullException(nameof(pluginAvailability));
    }

    public ScriptInstance RequireScript(string scriptId)
    {
        return _scripts.FindById(scriptId)
            ?? throw new InvalidOperationException($"脚本实例不存在：{scriptId}");
    }

    public DispatchQueue RequireQueue(string queueId)
    {
        return _queues.FindById(queueId)
            ?? throw new InvalidOperationException($"调度队列不存在：{queueId}");
    }

    public ExecutionResult Validate(ExecutionRequest request)
    {
        try
        {
            if (request.Kind == "script")
            {
                ScriptInstance script = RequireScript(request.TargetId);
                ValidateScriptStart(script, request.UserName);
                return new ExecutionResult(true, null, CountScriptTasks(script, request.UserName), script, null);
            }
            if (request.Kind == "queue")
            {
                DispatchQueue queue = RequireQueue(request.TargetId);
                ValidateQueueStart(queue);
                return new ExecutionResult(true, null, CountQueueTasks(queue), null, queue);
            }
            return new ExecutionResult(false, $"不支持的执行类型：{request.Kind}", 0, null, null);
        }
        catch (InvalidOperationException ex)
        {
            return new ExecutionResult(false, ex.Message, 0, null, null);
        }
    }

    public void ValidateScriptStart(ScriptInstance script, string? userName)
    {
        if (IsScriptRunning(script))
        {
            throw new InvalidOperationException($"脚本「{script.Name}」正在运行，请先退出后再执行");
        }

        string? timeoutError = Limits.CheckScriptTimeouts(
            script.LogStallTimeoutMinutes,
            script.TotalTimeoutMinutes);
        if (timeoutError is not null)
        {
            throw new InvalidOperationException(timeoutError);
        }

        if (PluginAvailability.GetUnavailableReason(script.PluginType, _pluginAvailability) is not null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(userName)
            && !string.IsNullOrWhiteSpace(script.ConfigPath)
            && _users.ResolveEnabledBinding(script, userName) is null)
        {
            throw new InvalidOperationException($"用户「{userName}」不存在或已禁用");
        }
        if (string.IsNullOrWhiteSpace(userName) && !_users.ResolveEnabledBindings(script).Any())
        {
            throw new InvalidOperationException($"脚本「{script.Name}」未配置启用用户，无法运行");
        }
    }

    public void ValidateQueueStart(DispatchQueue queue)
    {
        ValidateQueueStartSnapshot(queue, _scripts.Snapshot());
    }

    /// <summary>使用同一份脚本快照校验队列，供 ExecutionPlanBuilder 避免校验与计划读取之间漂移。</summary>
    public void ValidateQueueStartSnapshot(DispatchQueue queue, IReadOnlyList<ScriptInstance> scripts)
    {
        foreach (QueueTask task in queue.Tasks)
        {
            ScriptInstance? script = scripts.FirstOrDefault(s => s.Id == task.ScriptInstanceId);
            if (script is null)
            {
                continue;
            }

            string? timeoutError = Limits.CheckScriptTimeouts(
                script.LogStallTimeoutMinutes,
                script.TotalTimeoutMinutes);
            if (timeoutError is not null)
            {
                throw new InvalidOperationException($"脚本「{script.Name}」：{timeoutError}");
            }
        }

        string? mixError = Limits.CheckQueueMix(scripts, queue);
        if (mixError is not null)
        {
            throw new InvalidOperationException(mixError);
        }
    }

    public string? CheckQueueMix(DispatchQueue queue)
    {
        return Limits.CheckQueueMix(_scripts.Snapshot(), queue);
    }

    public int CountScriptTasks(ScriptInstance script, string? userName)
    {
        return string.IsNullOrWhiteSpace(userName)
            ? Math.Max(1, _users.ResolveEnabledBindings(script).Count)
            : 1;
    }

    public int CountQueueTasks(DispatchQueue queue)
    {
        int totalTasks = 0;
        foreach (QueueTask task in queue.Tasks)
        {
            ScriptInstance? script = _scripts.FindById(task.ScriptInstanceId);
            totalTasks += script is null ? 1 : Math.Max(1, _users.ResolveEnabledBindings(script).Count);
        }
        return totalTasks;
    }

    public IReadOnlyList<string> EnabledUserNames(ScriptInstance script)
    {
        return _users.ResolveEnabledBindings(script).Select(user => user.UserName).ToList();
    }

    public string? QueueBlockedBy(DispatchQueue queue)
    {
        foreach (QueueTask task in queue.Tasks.OrderBy(task => task.Index))
        {
            ScriptInstance? script = _scripts.FindById(task.ScriptInstanceId);
            if (IsScriptRunning(script))
            {
                return script!.Name;
            }
        }
        return null;
    }

    /// <summary>检查已经冻结的定时计划所引用的脚本进程，避免重试时回读并改变计划内容。</summary>
    public string? QueueBlockedByPlan(QueueExecutionPlan plan)
    {
        PlannedQueueTask? blocked = plan.Tasks.FirstOrDefault(task =>
            task.Script is not null && ExecutionValidator.IsScriptRunning(task.Script));
        return blocked?.Script?.Name;
    }

    /// <summary>按运行时启动目标进程名检测脚本冲突；Args 首项为显式路径时按它，否则使用主程序。</summary>
    public static bool IsScriptRunning(ScriptInstance? script)
    {
        if (script is null || string.IsNullOrWhiteSpace(script.MainExe))
        {
            return false;
        }
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        (string launchExe, _) = SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args);
        return SystemActions.IsExeRunning(launchExe);
    }
}
