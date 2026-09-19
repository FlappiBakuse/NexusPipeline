using NexusPipeline.Plugin.Abstractions;
namespace NexusPipeline.Modules.Execution.Contracts;


/// <summary>执行计划所需的单时刻仓储快照，队列与其脚本引用在同一数据锁内复制。</summary>
internal interface IExecutionSnapshotProvider
{
    ExecutionScriptSnapshot? SnapshotScript(string scriptId);

    ExecutionQueueSnapshot? SnapshotQueue(string queueId);
}
