namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionTargetBinding(Guid InstanceId, Guid ParentJournalId, Guid ParentOperationId,
    Guid AttemptId, string StepId, Guid TargetId, string JobName, Guid OwnerWriterId, int SessionId, string TotalPlanSha256);

internal sealed record TargetJobState(ExecutionTargetBinding Binding, string Stage, BlobReference? Evidence);

internal sealed record TargetJobIntent(string Format, int Version, ExecutionTargetBinding Binding,
    string Stage, BlobReference? Evidence);
