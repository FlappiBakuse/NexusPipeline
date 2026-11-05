using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Notifications;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledNotificationPort(InstalledExtensionConsumption consumption, INotificationSubmissionPort inner)
    : INotificationSubmissionPort
{
    public async ValueTask<NotificationAcceptance> SubmitAsync(NotificationSubmission submission, CancellationToken cancellationToken)
    {
        using var use = consumption.Begin();
        return await inner.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class LazyNotificationPort(PersistentScheduleService schedules, ExtensionManifest manifest,
    ExtensionScope scope, InstalledExtensionConsumption consumption) : INotificationSubmissionPort
{
    public ValueTask<NotificationAcceptance> SubmitAsync(NotificationSubmission submission, CancellationToken cancellationToken)
        => (consumption.Work is PreparedManualCallback manual ? schedules.CreateManualNotifications(manifest, scope, manual) :
            schedules.CreateNotificationPort(manifest, scope)).SubmitAsync(submission, cancellationToken);
}
