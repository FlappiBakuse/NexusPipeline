using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Execution;
namespace NexusPipeline.Modules.Execution.Contracts;


/// <summary>调度器使用的冻结队列计划入口；普通 Web/CLI 入口仍按 ID 构建即时计划。</summary>
internal interface IFrozenQueueExecutionService
{
    RunningExecution StartQueue(QueueExecutionPlan plan, string mode, string source);
}
