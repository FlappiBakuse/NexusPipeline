namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ExtensionActivationPreparation(string Format, int Version, Guid InstanceId,
    Guid OperationId, Guid JournalId, long BeforeRevision, BlobReference Source, BlobReference PhysicalResult,
    Guid ActivationId);

internal sealed record ActivationPreparationIntent(string Format, int Version,
    long BeforeRevision, BlobReference Artifact);
