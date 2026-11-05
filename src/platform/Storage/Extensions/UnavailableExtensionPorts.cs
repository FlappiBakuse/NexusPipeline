using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Notifications;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Extensibility.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class UnavailableExtensionPorts : IScheduleRegistrationPort, IExtensionSecretPort,
    INotificationSubmissionPort, IMediaPreviewRegistrationPort, IRunScreenshotPort
{
    public ValueTask RegisterAsync(string providerId, IScheduledWorkHandler handler, CancellationToken cancellationToken)
        => throw Unavailable();
    public ValueTask RegisterAsync(string providerId, IMediaFrameProvider provider, CancellationToken cancellationToken)
        => throw Unavailable();
    public ValueTask<SecretFieldState> GetStateAsync(Guid subjectId, string field, CancellationToken cancellationToken)
        => throw Unavailable();
    public ValueTask<SecretDraft> ReadAsync(Guid subjectId, string field, CancellationToken cancellationToken)
        => throw Unavailable();
    public ValueTask<SecretFieldState> ApplyAsync(Guid operationId, Guid subjectId, string field, long expectedRevision,
        SecretChange change, CancellationToken cancellationToken)
    { change?.Dispose(); throw Unavailable(); }
    public ValueTask<NotificationAcceptance> SubmitAsync(NotificationSubmission submission, CancellationToken cancellationToken)
        => throw Unavailable();
    public ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken)
        => throw Unavailable();
    private static IOException Unavailable() => new("extension_port_dependency_unavailable");
}
