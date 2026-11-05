using System.Collections.Immutable;

namespace NexusPipeline.Platform.Storage;

internal sealed record PayloadTreeScene(Guid OperationId, Guid JournalId, string Stage,
    PhysicalObjectIdentity? WorkspaceIdentity, ImmutableArray<PhysicalTreeObject> CandidateObjects);
