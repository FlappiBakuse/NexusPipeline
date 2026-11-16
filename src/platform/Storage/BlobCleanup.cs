using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage;

public sealed class BlobCleanup(RuntimeInstance instance, ImmutableBlobStore blobs, BlobReferenceIndex references)
{
    private PhysicalObjectDeletion.FileLease? _guarded;
    private BlobReference? _guardedBlob;
    private IReadOnlySet<BlobReference>? _guardedReferences;
    private CandidateProtection? _protection;

    internal IDisposable ProtectCandidates(IReadOnlyList<FrozenCleanupItem> items)
    {
        if (_protection is not null) throw new InvalidOperationException("cleanup_candidates_already_protected");
        var protection = new CandidateProtection(this);
        try
        {
            foreach (var item in items)
            {
                if (protection.Guards.ContainsKey(item.Blob)) continue;
                try { protection.Guards.Add(item.Blob, PhysicalObjectDeletion.HoldFile(Intent(item, Resolve(item)))); }
                catch (IOException) { }
            }
            _protection = protection;
            return protection;
        }
        catch { protection.Dispose(); throw; }
    }

    internal bool HasProtectedCandidates(IReadOnlyList<FrozenCleanupItem> items)
    {
        if (_protection is not { } protection || items.Count == 0) return false;
        try
        {
            foreach (var item in items)
            {
                if (!protection.Guards.TryGetValue(item.Blob, out var guard)) return false;
                guard.Require(Intent(item, Resolve(item)));
            }
            return true;
        }
        catch (IOException) { return false; }
    }

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
            var guarded = Guard(item, held);
            if (guarded is null)
            {
                if (PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!) != item.ParentIdentity) return CleanupItemState.Unknown;
                using var input = CleanupCandidateFiles.OpenVerified(instance, blobs, item.Blob);
                if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != item.Identity) return CleanupItemState.Unknown;
            }
            else guarded.Require(Intent(item, path));
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
        var guarded = Guard(item, held);
        var deleted = guarded is null ? PhysicalObjectDeletion.TryDeleteUnreferenced(intent, Unreferenced) :
            guarded.TryDelete(intent, Unreferenced);
        return deleted ? CleanupStepResult.Deleted : CleanupStepResult.SkippedReferenced;
    }

    private IReadOnlySet<BlobReference> ReadReferences(IReadOnlyList<FrozenCleanupItem> items, PhysicalObjectDeletion.FileLease? held)
    {
        if (held is null && _protection is { } protection)
        {
            var guarded = new HashSet<BlobReference>();
            var remaining = new HashSet<BlobReference>();
            foreach (var candidate in items)
            {
                if (Guard(candidate, null) is { } lease)
                { lease.Require(Intent(candidate, Resolve(candidate))); guarded.Add(candidate.Blob); }
                else remaining.Add(candidate.Blob);
            }
            var found = new HashSet<BlobReference>();
            if (guarded.Count > 0)
            {
                protection.References ??= references.ReadReferenced(protection.Guards.Keys.ToHashSet());
                found.UnionWith(protection.References.Where(guarded.Contains));
            }
            if (remaining.Count > 0) found.UnionWith(references.ReadReferenced(remaining));
            return found;
        }
        if (held is null || items.Count != 1) return references.ReadReferenced(items.Select(item => item.Blob).ToHashSet());
        var item = items[0]; held.Require(Intent(item, Resolve(item)));
        if (ReferenceEquals(held, _guarded) && item.Blob == _guardedBlob) return _guardedReferences!;
        // DELETE access prevents another verified reader from publishing a new reference until this exact guard closes.
        var result = references.ReadReferenced(new HashSet<BlobReference> { item.Blob });
        _guarded = held; _guardedBlob = item.Blob; _guardedReferences = result;
        return result;
    }

    private PhysicalObjectDeletion.FileLease? Guard(FrozenCleanupItem item, PhysicalObjectDeletion.FileLease? held)
        => held ?? (_protection?.Guards.TryGetValue(item.Blob, out var guarded) == true ? guarded : null);

    private sealed class CandidateProtection(BlobCleanup owner) : IDisposable
    {
        internal readonly Dictionary<BlobReference, PhysicalObjectDeletion.FileLease> Guards = new();
        internal IReadOnlySet<BlobReference>? References;

        public void Dispose()
        {
            if (ReferenceEquals(owner._protection, this)) owner._protection = null;
            foreach (var guard in Guards.Values) guard.Dispose();
            Guards.Clear(); References = null;
        }
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
