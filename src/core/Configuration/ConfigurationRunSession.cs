using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public enum ConfigurationRunPhase { Prepared, Running, Completed, Interrupted }
public sealed record RunResourceStatus(ConfigurationAddress Address, Guid OperationId, Guid JournalId,
    long ExpectedRevision, long CurrentRevision, ConfigurationContractStamp Contract, string PlanPath, string PlanDigest);
public sealed record ConfigurationRunStatus(Guid SessionId, Guid OwnerOperationId, Guid ParentJournalId, string StepId,
    long Revision, ConfigurationRunPhase Phase, long ExpectedBindingRevision, bool IsWriterAdmissionClosed,
    ImmutableArray<RunResourceStatus> Resources, bool IsNew)
{
    public int InvocationOrdinal { get; init; } = 1;
    public long SyncOrdinal { get; init; }
    public Guid? CompletionOperationId { get; init; }
    public bool EnteredRunning { get; init; }
    public long? ClosedElapsedMilliseconds { get; init; }
    public string? PrimaryJobName { get; init; }
}

public sealed record RunSyncItem(ConfigurationAddress Address, long PreviousRevision, long CurrentRevision, string ReasonCode);
public sealed record RunSyncResult(Guid OperationId, ConfigurationRunStatus Session,
    ImmutableArray<RunSyncItem> Items, bool IsNew);
