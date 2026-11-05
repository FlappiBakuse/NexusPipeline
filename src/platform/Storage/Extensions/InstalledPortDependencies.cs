using NexusPipeline.Core.Media;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

/// <summary>Explicit runtime owners; a missing dependency stays unavailable rather than being synthesized.</summary>
public sealed record InstalledPortDependencies(PersistentScheduleService? Scheduling,
    SecretFieldService? Secrets, ISecretAccessPolicy? SecretAuthorization, MediaPreviewRegistry? MediaPreviews,
    IMediaFrameSource? RunFrames, IRunMediaAuthorization? RunMediaAuthorization, Func<HttpClient>? CreateHttpClient = null,
    DesktopPresence? Desktop = null, NativeClientSessions? NativeClients = null, BrowserLoginSessions? BrowserLogins = null);
