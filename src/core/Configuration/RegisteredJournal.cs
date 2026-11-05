namespace NexusPipeline.Core.Configuration;

public sealed record RegisteredJournal(Guid JournalId, Guid OperationId, JournalKind Kind, JournalPhase Phase,
    long Revision, string PlanPath, string PlanDigest, IReadOnlyList<string> ResourceKeys);

public sealed record RecoveryRegistrySnapshot(long Revision, bool IsReady,
    IReadOnlyList<RegisteredJournal> Journals, IReadOnlyList<ResourceIsolation> Isolations, IReadOnlyList<FileEffect> FileEffects);
