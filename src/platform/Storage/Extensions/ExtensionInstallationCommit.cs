namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ExtensionInstallationCommit(string Format, int Version, Guid InstanceId,
    Guid OperationId, Guid JournalId, long BeforeRevision, BlobReference Source, BlobReference PhysicalResult,
    BlobReference Footprint, BlobReference? ActivationPreparation, long InstalledRevision, long SettingsBeforeRevision, long SettingsAfterRevision,
    bool DesiredEnabled, bool InitializeEnabledPreference, string ActivationState, Guid? ActivationId);

internal sealed record InstallationCommitIntent(string Format, int Version, long BeforeRevision,
    BlobReference Artifact);
