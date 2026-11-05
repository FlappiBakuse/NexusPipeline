using System.Collections.Immutable;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionTargetBinding(Guid InstanceId, Guid ParentJournalId, Guid ParentOperationId,
    Guid AttemptId, string StepId, Guid TargetId, string JobName, Guid OwnerWriterId, int SessionId, string TotalPlanSha256);

internal sealed record TargetProcessSnapshot(ExecutionTargetBinding Binding, ProcessIdentity? Root,
    ImmutableArray<ProcessIdentity> Family, WorkerJobAccounting Accounting, bool InventoryComplete);

internal sealed record TargetJobState(ExecutionTargetBinding Binding, string Stage, BlobReference? Evidence);

internal sealed record TargetJobIntent(string Format, int Version, ExecutionTargetBinding Binding,
    string Stage, BlobReference? Evidence);
