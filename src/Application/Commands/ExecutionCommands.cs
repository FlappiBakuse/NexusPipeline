using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.App.Abstractions;

namespace NexusPipeline.App.Commands;

/// <summary>执行应用命令入口。Web、CLI（经常驻 HTTP）和 Scheduler 共用同一组业务命令。</summary>
internal sealed class ExecutionCommands : IExecutionService
{
    private readonly DispatchCenter _center;

    public ExecutionCommands(DispatchCenter center)
    {
        _center = center;
    }

    public RunningExecution StartScript(string scriptId, string mode, string source, string? userName = null)
    {
        return _center.StartScript(scriptId, mode, source, userName);
    }

    public RunningExecution StartQueue(string queueId, string mode, string source)
    {
        return _center.StartQueue(queueId, mode, source);
    }

    public void Cancel(string runId, string source)
    {
        _center.Cancel(runId, source);
    }
}
