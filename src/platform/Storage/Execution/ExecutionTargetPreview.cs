using System.Runtime.Versioning;
using NexusPipeline.Core.Media;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal static class ExecutionTargetPreview
{
    internal static ExecutionLiveMedia.Step? Open(ExecutionLiveMedia? owner, RuntimeInstance instance,
        AcceptedExecutionAdmission accepted, InstanceLease lease, StepWriterScope scope, Func<bool> unavailable, string kind) =>
        owner?.Register(accepted.Identity.SubjectId,
            new(instance.InstanceId, Guid.Parse(accepted.Prepared.Total.Plan.ReadWire().RunId),
                Guid.Parse(accepted.Prepared.Attempt.Plan.ReadWire().AttemptId)), () =>
            {
                if (unavailable() || !accepted.Root.IsActive || accepted.Prepared.TimeBudget.Read().Remaining == TimeSpan.Zero)
                    throw new UnauthorizedAccessException("run_media_grant_required");
                accepted.Prepared.RequireLive();
                var current = lease.PublishExecution(scope.VerifyReadOnly);
                if (current.IsDispatchClosed) throw new UnauthorizedAccessException("run_media_grant_required");
            }, _ => throw new PreviewUnavailableException(kind == "android"
                ? PreviewAvailability.EmulatorNotReady : PreviewAvailability.WaitingForGame),
            kind == "android" ? "emulator" : "pc_game");
}
