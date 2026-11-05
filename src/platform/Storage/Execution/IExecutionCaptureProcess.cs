using System.Runtime.Versioning;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal interface IExecutionCaptureProcess : ILiveCaptureProcess
{
    string JobName { get; }
    T UseStopped<T>(ImmutableBlobStore blobs,
        Func<OwnedWorkerIdentity?, OwnedTargetInventory, BlobReference?, string, CapturePngInfo?, T> publish);
}
