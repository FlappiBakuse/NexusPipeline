namespace NexusPipeline.Extensibility.Notifications;

/// <summary>Lists existing target identities without exposing channel configuration or credentials.</summary>
public interface INotificationTargets
{
    /// <summary>Returns one current nonsecret catalog snapshot; this does not authorize delivery.</summary>
    ValueTask<NotificationTargets> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Disabled targets remain visible so saved selections can retain their exact identities.</summary>
public sealed record NotificationTargetInfo(Guid TaskId, long Revision, string Name, bool IsEnabled, bool IsConfigured, string Kind);

/// <summary>The catalog revision belongs to the existing Host notification owner.</summary>
public sealed record NotificationTargets(long Revision, IReadOnlyList<NotificationTargetInfo> Items);
