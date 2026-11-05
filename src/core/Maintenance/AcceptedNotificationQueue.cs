namespace NexusPipeline.Core.Maintenance;

public sealed class AcceptedNotificationQueue : IDisposable
{
    private readonly HostLifecycle _lifecycle;
    internal ResourceLeaseManager ResourceOwner { get; }
    public Guid OperationId { get; }
    public bool IsActive => _lifecycle.IsQueueActive(this);

    internal AcceptedNotificationQueue(HostLifecycle lifecycle, ResourceLeaseManager owner, Guid operationId)
    { _lifecycle = lifecycle; ResourceOwner = owner; OperationId = operationId; }

    public void Dispose() => _lifecycle.ReleaseNotificationQueue(this);
}

internal static class NotificationQueueResources
{
    internal const string Queue = "notifications:queue";
    internal static IReadOnlyList<ResourceClaim> SenderClaims { get; } = Array.AsReadOnly(new ResourceClaim[]
    {
        new("notifications:catalog", ResourceClaimMode.Reference), new("secrets:fields", ResourceClaimMode.Reference),
        new("settings:host", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)
    });
}
