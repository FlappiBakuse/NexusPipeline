using NexusPipeline.Extensibility.Assets;
using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Notifications;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Extensibility.Secrets;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Extensibility.Networking;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Extensibility.Emulation;

namespace NexusPipeline.Extensibility.Lifecycle;

/// <summary>Explicit Host ports bound to one activation; no service lookup or authority is carried by this value alone.</summary>
public sealed class ExtensionActivationContext
{
    /// <summary>Registrations remain invisible until this activation is published.</summary>
    public IScheduleRegistrationPort Schedules { get; }
    /// <summary>Secret access still requires declared subjects and current host authorization.</summary>
    public IExtensionSecretPort Secrets { get; }
    /// <summary>Acceptance binds saved targets and never asserts external delivery.</summary>
    public INotificationSubmissionPort Notifications { get; }
    /// <summary>Provider callbacks are drained before activation resources can be released.</summary>
    public IMediaPreviewRegistrationPort MediaPreviews { get; }
    /// <summary>Every request remains bound to current run and host media authorization.</summary>
    public IRunScreenshotPort RunScreenshots { get; }
    /// <summary>Only the original verified package tree is exposed through owned read leases.</summary>
    public IExtensionPackageAssets PackageAssets { get; }
    /// <summary>Writable output paths belong to this activation rather than its package.</summary>
    public IExtensionRuntimeOutputs RuntimeOutputs { get; }
    /// <summary>Document access still requires the actual manifest's instance-data permission.</summary>
    public IExtensionDocuments? Documents { get; }
    /// <summary>A missing network owner remains unavailable; manifest origins never create one.</summary>
    public IExtensionHttp? Http { get; }
    /// <summary>Image commits retain the same document CAS and instance-data authority.</summary>
    public IExtensionImages? Images { get; }
    /// <summary>Browser and remote clients cannot manufacture a native desktop session identity.</summary>
    public IExtensionDesktop? Desktop { get; }
    /// <summary>Completes after the committed installation writer and all of its resource borrows release.</summary>
    public IExtensionActivation? Activation { get; }
    /// <summary>Coordinates ordinary JSON with the existing secret owner without exposing partially published fields.</summary>
    public ISecretDocuments? SecretDocuments { get; }
    /// <summary>Reads existing target metadata without recipients, templates or protected credentials.</summary>
    public INotificationTargets? NotificationTargets { get; }
    /// <summary>Work control is available only with the actual Host scheduling owner.</summary>
    public IWorkTasks? WorkTasks { get; }
    /// <summary>Durable external effects require the actual work and secret owners.</summary>
    public IWorkEffects? WorkEffects { get; }
    /// <summary>Compilation remains unavailable without a Host registry and a declared provider.</summary>
    public ICompilerRegistration? Compilers { get; }
    /// <summary>Vendor providers remain unavailable without their manifest declaration and actual Host registry.</summary>
    public IEmulatorRegistration? Emulators { get; }

    /// <summary>The host supplies one immutable binding so an extension cannot select another service owner.</summary>
    public ExtensionActivationContext(IScheduleRegistrationPort schedules, IExtensionSecretPort secrets,
        INotificationSubmissionPort notifications, IMediaPreviewRegistrationPort mediaPreviews,
        IRunScreenshotPort runScreenshots, IExtensionPackageAssets packageAssets, IExtensionRuntimeOutputs runtimeOutputs,
        IExtensionDocuments? documents = null, IExtensionHttp? http = null, IExtensionImages? images = null,
        IExtensionDesktop? desktop = null, IExtensionActivation? activation = null, ISecretDocuments? secretDocuments = null,
        INotificationTargets? notificationTargets = null, IWorkTasks? workTasks = null, IWorkEffects? workEffects = null,
        ICompilerRegistration? compilers = null, IEmulatorRegistration? emulators = null)
    {
        Schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        Secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        MediaPreviews = mediaPreviews ?? throw new ArgumentNullException(nameof(mediaPreviews));
        RunScreenshots = runScreenshots ?? throw new ArgumentNullException(nameof(runScreenshots));
        PackageAssets = packageAssets ?? throw new ArgumentNullException(nameof(packageAssets));
        RuntimeOutputs = runtimeOutputs ?? throw new ArgumentNullException(nameof(runtimeOutputs));
        Documents = documents; Http = http; Images = images; Desktop = desktop; Activation = activation; SecretDocuments = secretDocuments;
        NotificationTargets = notificationTargets;
        WorkTasks = workTasks;
        WorkEffects = workEffects;
        Compilers = compilers;
        Emulators = emulators;
    }
}
