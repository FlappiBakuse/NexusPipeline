using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ExtensionInstalledFootprint(string Format, int Version, Guid InstanceId,
    Guid OperationId, Guid JournalId, string ExtensionId, long Revision, bool IsInstalled,
    ExtensionPackageIdentity Package, string TargetPath, PhysicalObjectIdentity ParentIdentity,
    BlobReference Source, BlobReference PhysicalResult, ArchivedFileTree Payload);
