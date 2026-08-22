using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 执行门面：保持既有 Web/CLI/Scheduler 入口不变，只负责门禁、运行登记和取消。
/// 具体校验由 <see cref="ExecutionValidator"/> 负责，后台生命周期由 <see cref="ExecutionRunner"/> 负责。
/// </summary>
internal sealed class DispatchCenter
{
    private readonly ExecutionStateStore _state;
    private readonly ExecutionPlanBuilder _plans;
    private readonly ExecutionRunner _runner;
    private readonly SystemActionExecutor _systemActions;

    public DispatchCenter(
        ExecutionStateStore state,
        ExecutionPlanBuilder plans,
        ExecutionRunner runner,
        SystemActionExecutor systemActions)
    {
        _state = state;
        _plans = plans;
        _runner = runner;
        _systemActions = systemActions;
    }

    public IReadOnlyList<RunningExecution> Active => _state.Active;

    public RunningExecution? Find(string id) => _state.Find(id);

    /// <summary>查找运行任务：先查运行中列表，再查已结束列表，供 CLI 轮询结果。</summary>
    public RunningExecution? FindAny(string id) => _state.FindAny(id);

    /// <summary>当前待执行的系统操作，供 Web 展示倒计时和取消入口。</summary>
    public PendingSystemAction? CurrentSystemAction => _systemActions.Current;

    public bool CancelSystemAction() => _systemActions.Cancel(Audit.Web);

    public RunningExecution StartScript(string scriptId, string mode, string source = Audit.System, string? userName = null)
    {
        ScriptExecutionPlan plan = _plans.BuildScript(scriptId, userName);
        ScriptInstance script = plan.Script;
        var exec = new RunningExecution
        {
            Kind = "script",
            TargetId = script.Id,
            TargetName = script.Name,
            Mode = mode,
            TotalTasks = plan.TotalTasks,
            CurrentScriptName = script.Name,
        };
        Register(exec, plan.Admission, source);
        exec.CurrentStatus = "排队等待中...";
        Task task = Task.Run(() => _runner.RunScriptAsync(exec, plan));
        exec.Completion = task;
        return exec;
    }

    public RunningExecution StartQueue(string queueId, string mode, string source = Audit.System)
    {
        QueueExecutionPlan plan = _plans.BuildQueue(queueId);
        DispatchQueue queue = plan.Queue;
        var exec = new RunningExecution
        {
            Kind = "queue",
            TargetId = queue.Id,
            TargetName = queue.Name,
            Mode = mode,
            TotalTasks = plan.TotalTasks,
        };
        Register(exec, plan.Admission, source);
        Task task = Task.Run(() => _runner.RunQueueAsync(exec, plan));
        exec.Completion = task;
        return exec;
    }

    public void Cancel(string runId, string source = Audit.System)
    {
        RunningExecution? exec = Find(runId);
        if (exec is null)
        {
            throw new InvalidOperationException($"未找到运行中的任务：{runId}");
        }
        Audit.Log(source, $"取消运行{ExecKindText(exec)}", exec.TargetName);
        try
        {
            exec.Cts.Cancel();
        }
        catch (Exception ex)
        {
            Logger.Warn($"取消信号发送失败（{exec.TargetName}），任务可能仍在运行：{ex.Message}");
        }
    }

    /// <summary>兼容现有 CLI/内部调用方的静态进程检测入口。</summary>
    public static bool IsScriptRunning(ScriptInstance? script) => ExecutionValidator.IsScriptRunning(script);

    private void Register(RunningExecution exec, ExecutionAdmissionProfile profile, string source)
    {
        if (!_state.TryRegister(exec, profile, out ExecutionAdmissionFailure? failure))
        {
            throw new ExecutionAdmissionException(failure!);
        }
        Audit.Log(source, $"执行{ExecKindText(exec)}", $"{exec.TargetName}（模式：{(exec.Mode == "auto" ? "自动" : "手动")}）");
    }

    private static string ExecKindText(RunningExecution exec)
    {
        return exec.Kind == "queue" ? "调度队列" : "脚本实例";
    }
}
