using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Scheduling;
namespace NexusPipeline.Modules.Execution.Contracts;


/// <summary>执行应用端口，供 Web、CLI、Scheduler 共享执行入口。</summary>
internal interface IExecutionService
{
    RunningExecution StartScript(string scriptId, string mode, string source, string? userName = null);

    RunningExecution StartQueue(string queueId, string mode, string source);

    void Cancel(string runId, string source);
}
