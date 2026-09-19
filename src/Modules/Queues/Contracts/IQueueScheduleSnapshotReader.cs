using NexusPipeline.Modules.Queues;

namespace NexusPipeline.Modules.Queues.Contracts;

/// <summary>Scheduler-facing queue snapshot port.</summary>
internal interface IQueueScheduleSnapshotReader
{
    IReadOnlyList<DispatchQueue> SnapshotForScheduling();
}
