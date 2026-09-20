namespace NexusPipeline.Modules.Queues.Contracts;

/// <summary>Plugin-scoped data cleanup port for queue deletion.</summary>
internal interface IQueueDataMaintenance
{
    void RemoveQueueData(string queueId);
}
