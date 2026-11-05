using System.Collections.Immutable;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ExtensionPhysicalRecord(string Format, int Version, Guid InstanceId, Guid OperationId,
    Guid JournalId, string PlanDigest, BlobReference Source, long BeforeRevision, string Stage,
    Guid? PreviousActivationId, string PreviousScopeClosure, PhysicalObjectIdentity? WorkspaceIdentity,
    ImmutableArray<PhysicalTreeObject> CandidateObjects);

internal sealed record ExtensionPhysicalIntent(string Format, int Version, long BeforeRevision,
    string Stage, BlobReference Artifact);

internal sealed record ExtensionPhysicalEntry(ExtensionPhysicalIntent Intent,
    ExtensionPhysicalRecord Record);
