using NexusPipeline.Extensibility.Assets;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Notifications;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Extensibility.Secrets;

internal static class SliceActivationContext
{
    internal static ExtensionActivationContext Create(IScheduleRegistrationPort? schedules = null,
        IExtensionSecretPort? secrets = null, INotificationSubmissionPort? notifications = null,
        IMediaPreviewRegistrationPort? previews = null, IRunScreenshotPort? screenshots = null,
        IExtensionPackageAssets? assets = null, IExtensionRuntimeOutputs? outputs = null) =>
        new(schedules ?? Unavailable.Instance, secrets ?? Unavailable.Instance, notifications ?? Unavailable.Instance,
            previews ?? Unavailable.Instance, screenshots ?? Unavailable.Instance, assets ?? Unavailable.Instance,
            outputs ?? Unavailable.Instance);

    private sealed class Unavailable : IScheduleRegistrationPort, IExtensionSecretPort, INotificationSubmissionPort,
        IMediaPreviewRegistrationPort, IRunScreenshotPort, IExtensionPackageAssets, IExtensionRuntimeOutputs
    {
        internal static readonly Unavailable Instance = new();
        public ValueTask RegisterAsync(string providerId, IScheduledWorkHandler handler, CancellationToken cancellationToken) => throw Denied();
        public ValueTask RegisterAsync(string providerId, IMediaFrameProvider provider, CancellationToken cancellationToken) => throw Denied();
        public ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken) => throw Denied();
        public ValueTask<NotificationAcceptance> SubmitAsync(NotificationSubmission submission, CancellationToken cancellationToken) => throw Denied();
        public ValueTask<SecretFieldState> GetStateAsync(Guid subjectId, string field, CancellationToken cancellationToken) => throw Denied();
        public ValueTask<SecretDraft> ReadAsync(Guid subjectId, string field, CancellationToken cancellationToken) => throw Denied();
        public ValueTask<SecretFieldState> ApplyAsync(Guid operationId, Guid subjectId, string field, long expectedRevision,
            SecretChange change, CancellationToken cancellationToken) { change.Dispose(); throw Denied(); }
        public ValueTask<IExtensionPackageReadLease> AcquireAsync(CancellationToken cancellationToken) => throw Denied();
        public ValueTask<IExtensionOutputLease> CreateAsync(string portableName, CancellationToken cancellationToken) => throw Denied();
        private static IOException Denied() => new("slice_component_port_not_supplied");
    }
}
