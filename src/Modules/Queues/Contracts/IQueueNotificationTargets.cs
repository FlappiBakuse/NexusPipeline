namespace NexusPipeline.Modules.Queues.Contracts;

internal interface IQueueNotificationTargets
{
    string? Validate(IEnumerable<string>? ids, IEnumerable<string>? previous = null);
}
