using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Notifications;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledNotificationTargets(InstalledExtensionConsumption consumption, NotificationCatalogService catalog, bool allowed)
    : INotificationTargets
{
    public async ValueTask<NotificationTargets> ReadAsync(CancellationToken cancellationToken)
    {
        if (!allowed) throw new UnauthorizedAccessException("notification_submit_capability_required");
        using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        var current = catalog.ReadTargets();
        var result = new NotificationTargets(current.Revision, Array.AsReadOnly(current.Tasks.Select(task =>
            new NotificationTargetInfo(task.TaskId, task.Revision, task.Name, task.IsEnabled, task.IsConfigured, task.Kind.ToString().ToLowerInvariant())).ToArray()));
        use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
}
