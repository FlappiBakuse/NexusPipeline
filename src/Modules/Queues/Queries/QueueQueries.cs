using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Queues.Contracts;

namespace NexusPipeline.Modules.Queues.Queries;

internal sealed record QueueReadModel(
    DispatchQueue Queue,
    DateTime? NextTrigger);

/// <summary>队列读取用例：把调度计算与运行时实体快照组合成 Web/CLI 可消费的读取模型。</summary>
internal sealed class QueueQueries
{
    private readonly IQueueRepository _queues;
    private readonly IQueueScheduleProjection _schedule;

    public QueueQueries(IQueueRepository queues, IQueueScheduleProjection schedule)
    {
        _queues = queues;
        _schedule = schedule;
    }

    public IReadOnlyList<QueueReadModel> List()
    {
        return _queues.Snapshot()
            .OrderBy(queue => queue.Index)
            .Select(queue => new QueueReadModel(queue, _schedule.NextTriggerFor(queue)))
            .ToList();
    }

    public QueueReadModel? Find(string id)
    {
        DispatchQueue? queue = _queues.FindById(id);
        return queue is null ? null : new QueueReadModel(queue, _schedule.NextTriggerFor(queue));
    }
}
