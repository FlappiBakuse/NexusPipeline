using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Queues;
namespace NexusPipeline.Modules.Queues.Contracts;


/// <summary>调度队列读取端口。写入仍由 Web/CLI 的现有事务路径负责。</summary>
internal interface IQueueRepository
{
    DispatchQueue? FindById(string id);

    IReadOnlyList<DispatchQueue> Snapshot();
}
