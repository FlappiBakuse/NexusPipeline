namespace NexusPipeline.Platform.Storage.Notifications;

internal enum NotificationTransportOutcome { Delivered, Rejected, Unknown }
internal sealed record NotificationTransportResult(NotificationTransportOutcome Outcome, int? StatusCode)
{
    internal NotificationDeliveryState State => Outcome switch
    {
        NotificationTransportOutcome.Delivered => NotificationDeliveryState.Succeeded,
        NotificationTransportOutcome.Rejected => NotificationDeliveryState.Failed,
        _ => NotificationDeliveryState.Unknown
    };
}
