using System.Runtime.Versioning;
using NexusPipeline.Core.Media;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal interface ILiveCaptureProcess : IDisposable
{
    Task CaptureAsync(Func<Func<OwnedWorkerJob, OwnedWorkerIdentity>, OwnedWorkerIdentity> useParent,
        Action<OwnedWorkerJob, OwnedWorkerIdentity> admitRoot, TimeSpan stopTimeout, CancellationToken cancellation);
    Task FinishAsync(TimeSpan stopTimeout);
    PreviewImage ReadLiveFrame();
    void RequireFramePublication();
}
