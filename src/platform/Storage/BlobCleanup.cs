using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage;

public sealed class BlobCleanup(RuntimeInstance instance, ImmutableBlobStore blobs, BlobReferenceIndex references)
{
    private PhysicalObjectDeletion.FileLease? _guarded;
    private BlobReference? _guardedBlob;
    private IReadOnlySet<BlobReference>? _guardedReferences;
    public CleanupItemState Inspect(FrozenCleanupItem item)
        => Inspect(item, null, null);

    internal IReadOnlyDictionary<string, CleanupItemState> Inspect(IReadOnlyList<FrozenCleanupItem> items,
        PhysicalObjectDeletion.FileLease? held = null, bool readReferences = true)
    {
        if (items.Count == 0) return new Dictionary<string, CleanupItemState>();
        try
        {
            var referenced = readReferences ? ReadReferences(items, held) : items.Select(item => item.Blob).ToHashSet();
            return items.ToDictionary(item => item.StepId, item => Inspect(item, referenced, held), StringComparer.Ordinal);
        }
        catch (IOException)
        { return items.ToDictionary(item => item.StepId, _ => CleanupItemState.Unknown, StringComparer.Ordinal); }
    }

    internal CleanupItemState Inspect(FrozenCleanupItem item, PhysicalObjectDeletion.FileLease? held)
        => Inspect(item, null, held);

    private CleanupItemState Inspect(FrozenCleanupItem item, IReadOnlySet<BlobReference>? referenced,
        PhysicalObjectDeletion.FileLease? held)
    {
        try
        {
            var path = Resolve(item);
            if (held is null)
            {
                if (PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!) != item.ParentIdentity) return CleanupItemState.Unknown;
                using var input = CleanupCandidateFiles.OpenVerified(instance, blobs, item.Blob);
                if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != item.Identity) return CleanupItemState.Unknown;
            }
            else held.Require(Intent(item, path));
            return (referenced?.Contains(item.Blob) ?? ReadReferences([item], held).Contains(item.Blob))
                ? CleanupItemState.Referenced : CleanupItemState.VerifiedUnreferenced;
        }
        catch (IOException) { return CleanupItemState.Unknown; }
    }

    public CleanupStepResult RemoveVerified(FrozenCleanupItem item)
        => RemoveVerified(item, null);

    internal CleanupStepResult RemoveVerified(FrozenCleanupItem item, PhysicalObjectDeletion.FileLease? held)
    {
        var intent = Intent(item, Resolve(item));
        bool Unreferenced() => !ReadReferences([item], held).Contains(item.Blob);
        var deleted = held is null ? PhysicalObjectDeletion.TryDeleteUnreferenced(intent, Unreferenced) :
            held.TryDelete(intent, Unreferenced);
        return deleted ? CleanupStepResult.Deleted : CleanupStepResult.SkippedReferenced;
    }

    private IReadOnlySet<BlobReference> ReadReferences(IReadOnlyList<FrozenCleanupItem> items, PhysicalObjectDeletion.FileLease? held)
    {
        if (held is null || items.Count != 1) return references.ReadReferenced(items.Select(item => item.Blob).ToHashSet());
        var item = items[0]; held.Require(Intent(item, Resolve(item)));
        if (ReferenceEquals(held, _guarded) && item.Blob == _guardedBlob) return _guardedReferences!;
        // DELETE access prevents another verified reader from publishing a new reference until this exact guard closes.
        var result = references.ReadReferenced(new HashSet<BlobReference> { item.Blob });
        _guarded = held; _guardedBlob = item.Blob; _guardedReferences = result;
        return result;
    }

    internal static FileObjectDeletion Intent(FrozenCleanupItem item, string path) => new(path, item.ParentIdentity,
        item.Identity, FileObjectKind.File, item.Blob.SizeBytes, item.Blob.Sha256);

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
