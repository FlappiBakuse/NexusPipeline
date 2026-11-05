using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal enum AndroidResponseCompletion { Unverified, NaturalRootExitAndEofBeforeStop, OwnedLaunchHandoff }

internal sealed record AndroidCommandRegistration(string Format, int Version, ExecutionTargetBinding Binding,
    int Sequence, AndroidCommandPurpose Purpose, ExecutionAndroidAction Action, BlobReference Request,
    string RequestDigest, BlobReference ControlPlan, BlobReference? PreviousObservation);
internal sealed record AndroidCommandObservation(string Format, int Version, ExecutionTargetBinding Binding,
    string RequestDigest, ProcessIdentity? Root, int? ExitCode, bool StartReturned, bool OutputComplete,
    ImmutableArray<TargetCommandPart> Parts, EngineOutcome Engine, long? ElapsedMilliseconds,
    AndroidResponseCompletion Completion);
internal sealed record AndroidCommandCollection(string Format, int Version, ExecutionTargetBinding Binding,
    string RequestDigest, BlobReference Observation, OwnedTargetInventory Inventory)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BlobReference? Retention { get; init; }
}
internal sealed record AndroidControlPlan(string Format, int Version, Guid InstanceId, Guid ParentJournalId,
    Guid ParentOperationId, Guid AttemptId, string StepId, string TotalPlanSha256, AndroidControlLimits Limits);
