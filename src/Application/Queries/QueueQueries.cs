using NexusPipeline.App.State;
using NexusPipeline.Models;
using NexusPipeline.Services;

namespace NexusPipeline.App.Queries;

internal sealed record QueueReadModel(
    DispatchQueue Queue,
    DateTime? NextTrigger);

/// <summary>队列读取用例：把调度计算与运行时实体快照组合成 Web/CLI 可消费的读取模型。</summary>
internal sealed class QueueQueries
{
    private readonly RuntimeEntityState _state;
    private readonly Scheduler _scheduler;

    public QueueQueries(RuntimeEntityState state, Scheduler scheduler)
    {
        _state = state;
        _scheduler = scheduler;
    }

    public IReadOnlyList<QueueReadModel> List()
    {
        return _state.SnapshotQueues()
            .OrderBy(queue => queue.Index)
            .Select(queue => new QueueReadModel(queue, _scheduler.NextTriggerFor(queue)))
            .ToList();
    }

    public QueueReadModel? Find(string id)
    {
        DispatchQueue? queue = _state.FindQueue(id);
        return queue is null ? null : new QueueReadModel(queue, _scheduler.NextTriggerFor(queue));
    }
}
