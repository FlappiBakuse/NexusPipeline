namespace NexusPipeline.Modules.Queues.Contracts;

/// <summary>Notification port for invalidating scheduler plans after queue writes.</summary>
internal interface IQueuePlansChanged
{
    void RevalidatePendingPlans();
}
