using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal static class ExecutionCaptureStarter
{
    internal static OwnedWorkerIdentity Start(OwnedWorkerJob helper, OwnedWorkerJob main, OwnedPreviewJob? preview,
        string executable, IReadOnlyList<string> arguments, string directory, Guid instanceId, Guid attemptId,
        Action<OwnedWorkerIdentity> admit, OwnedProcessOutput output) => preview is null
        ? helper.StartNested(main, executable, arguments, directory, instanceId, attemptId, admit, output)
        : helper.StartPreview(preview, executable, arguments, directory, instanceId, attemptId, admit, output);
}
