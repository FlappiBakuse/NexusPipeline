namespace NexusPipeline.Platform.Storage.Notifications;

public sealed record RunDeliveryStatus(bool IsRegistered, bool IsExpanded,
    int Queued, int Sending, int Succeeded, int Failed, int Unknown, int Skipped)
{
    public bool IsSettled => IsRegistered && IsExpanded && Queued == 0 && Sending == 0 && Unknown == 0;
}
