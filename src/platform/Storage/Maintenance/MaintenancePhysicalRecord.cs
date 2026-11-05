using System.Collections.Immutable;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record MaintenancePhysicalRecord(string Format, int Version, Guid InstanceId, Guid OperationId,
    Guid JournalId, string PlanDigest, BlobReference Source, long BeforeRevision, string Stage,
    PhysicalObjectIdentity? WorkspaceIdentity,
    ImmutableArray<PhysicalTreeObject> CandidateObjects);

internal sealed record MaintenancePhysicalIntent(string Format, int Version, long BeforeRevision,
    string Stage, BlobReference Artifact);

internal sealed record MaintenancePhysicalEntry(MaintenancePhysicalIntent Intent,
    MaintenancePhysicalRecord Record);
