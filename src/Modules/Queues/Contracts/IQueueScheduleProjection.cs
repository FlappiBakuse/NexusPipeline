using NexusPipeline.Modules.Queues;

namespace NexusPipeline.Modules.Queues.Contracts;

/// <summary>队列读取用例所需的下一次触发时间投影；具体调度算法由宿主绑定。</summary>
internal interface IQueueScheduleProjection
{
    DateTime? NextTriggerFor(DispatchQueue queue);
}
