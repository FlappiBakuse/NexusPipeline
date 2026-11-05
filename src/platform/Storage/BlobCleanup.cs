using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage;

public sealed class BlobCleanup(RuntimeInstance instance, ImmutableBlobStore blobs, BlobReferenceIndex references)
{
    public CleanupItemState Inspect(FrozenCleanupItem item)
        => Inspect(item, null);

    internal IReadOnlyDictionary<string, CleanupItemState> Inspect(IReadOnlyList<FrozenCleanupItem> items)
    {
        if (items.Count == 0) return new Dictionary<string, CleanupItemState>();
        try
        {
            var referenced = references.ReadReferenced(items.Select(item => item.Blob).ToHashSet());
            return items.ToDictionary(item => item.StepId, item => Inspect(item, referenced), StringComparer.Ordinal);
        }
        catch (IOException)
        { return items.ToDictionary(item => item.StepId, _ => CleanupItemState.Unknown, StringComparer.Ordinal); }
    }

    private CleanupItemState Inspect(FrozenCleanupItem item, IReadOnlySet<BlobReference>? referenced)
    {
        try
        {
            var path = Resolve(item);
            if (PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!) != item.ParentIdentity) return CleanupItemState.Unknown;
            using var input = CleanupCandidateFiles.OpenVerified(instance, blobs, item.Blob);
            if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != item.Identity) return CleanupItemState.Unknown;
            return (referenced?.Contains(item.Blob) ?? references.IsReferenced(item.Blob))
                ? CleanupItemState.Referenced : CleanupItemState.VerifiedUnreferenced;
        }
        catch (IOException) { return CleanupItemState.Unknown; }
    }

    public CleanupStepResult RemoveVerified(FrozenCleanupItem item)
    {
        var path = Resolve(item);
        var deleted = PhysicalObjectDeletion.TryDeleteUnreferenced(new(path, item.ParentIdentity, item.Identity,
            FileObjectKind.File, item.Blob.SizeBytes, item.Blob.Sha256), () => !references.IsReferenced(item.Blob));
        return deleted ? CleanupStepResult.Deleted : CleanupStepResult.SkippedReferenced;
    }

    private string Resolve(FrozenCleanupItem item)
    {
        CleanupCandidateFiles.Validate(item.Blob);
        if (blobs.Instance.InstanceId != instance.InstanceId ||
            !string.Equals(blobs.Instance.RuntimeRoot, instance.RuntimeRoot, StringComparison.OrdinalIgnoreCase) ||
            references.Instance.InstanceId != instance.InstanceId ||
            !string.Equals(references.Instance.RuntimeRoot, instance.RuntimeRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("blob_cleanup_instance_or_path_unbound");
        return PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, item.Blob.RelativePath));
    }
}
