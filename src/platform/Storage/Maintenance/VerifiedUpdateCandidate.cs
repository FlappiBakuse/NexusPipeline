using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Archives;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class VerifiedUpdateCandidate : IDisposable
{
    private readonly object _gate = new();
    private readonly UpdateFeedStore _feeds;
    private readonly UpdateFeedIdentity _feed;
    private readonly FileStream _input;
    private readonly StableDirectoryLease _parent;
    private readonly PayloadArchive _archive;
    private readonly PhysicalObjectIdentity _identity, _parentIdentity;
    private readonly Guid _operationId;
    private readonly byte[] _manifest;
    private readonly UpdateRecipe _recipe;
    private readonly ContractReader _contracts;
    private bool _disposed;
    internal HostUpdateRelease Release { get; }
    internal UpdatePayloadManifest Manifest { get; }
    internal string ArchivePath { get; }

    private VerifiedUpdateCandidate(UpdateFeedStore feeds, UpdateFeedIdentity feed, Guid operationId,
        string path, FileStream input, StableDirectoryLease parent, PayloadArchive archive, byte[] original,
        UpdatePayloadManifest manifest, HostUpdateRelease release, UpdateRecipe recipe, ContractReader contracts)
    {
        _feeds = feeds; _feed = feed; _operationId = operationId; ArchivePath = path; _input = input; _parent = parent;
        _archive = archive; _manifest = original.ToArray(); Manifest = manifest; Release = release;
        _recipe = recipe; _contracts = contracts;
        _identity = PhysicalObjectIdentity.Read(input.SafeFileHandle);
        _parentIdentity = PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!);
    }

    internal static VerifiedUpdateCandidate Prepare(RuntimeInstance instance, UpdateFeedStore feeds,
        ResourceOperationLease root, UpdateFeedIdentity feed, string hostVersion, ContractReader contracts,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (root.OperationId == Guid.Empty || instance != feeds.Instance) throw Invalid();
        var accepted = feeds.RequireRelease(root, feed, hostVersion);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "staging",
            "host-update-" + root.OperationId.ToString("N"), "payload.zip"));
        var parent = StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!);
        FileStream? input = null; PayloadArchive? archive = null;
        try
        {
            PhysicalObjectIdentity.RequireSingleLink(path);
            input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != accepted.Release.Artifact.SizeBytes ||
                Convert.ToHexStringLower(SHA256.HashData(input)) != accepted.Release.Artifact.Sha256) throw Invalid();
            input.Position = 0;
            archive = PayloadArchive.Open(input, new(4096, 1073741824), Invalid, PayloadArchiveKind.Application, cancellation);
            var bytes = archive.ReadManifest(cancellation);
            var manifest = new UpdateManifestCodec(contracts).Read(bytes, accepted.Release, archive.Files, feeds.PayloadProfile);
            var recipe = UpdateRecipe.Read(archive, manifest, contracts);
            var result = new VerifiedUpdateCandidate(feeds, feed, root.OperationId, path, input, parent, archive, bytes,
                manifest, accepted.Release, recipe, contracts);
            result.RequireCurrent(root, cancellation); return result;
        }
        catch { archive?.Dispose(); input?.Dispose(); parent.Dispose(); throw; }
    }

    internal T Capture<T>(ResourceOperationLease root, ImmutableBlobStore blobs, ArchivedFileTree before, CancellationToken cancellation,
        Func<SavedUpdateFeed, HostUpdateRelease, BlobReference, BlobReference, ArchivedFileTree, T> consume)
    {
        lock (_gate)
        {
            using var borrow = _feeds.BorrowCandidate(root, _operationId);
            return borrow.Use(() =>
            {
                if (blobs.Instance != _feeds.Instance) throw Invalid();
                RequireCurrent(root, cancellation);
                var accepted = _feeds.RequireRelease(root, _feed, Release.HostVersion);
                var contents = _recipe.Capture(_archive, before, Manifest, accepted.Feed, blobs, _contracts, cancellation);
                UpdateBuildProfile.Require(Manifest, contents, blobs, _feeds.PayloadProfile);
                var manifest = blobs.PersistPlan(_manifest);
                var archive = blobs.PersistContent(ArchivePath, new("payload.zip", NexusPipeline.Core.Configuration.FileObjectKind.File,
                    Release.Artifact.SizeBytes, Release.Artifact.Sha256), cancellation);
                RequireCurrent(root, cancellation);
                return consume(accepted.Saved, Release, manifest, archive, contents);
            });
        }
    }

    private void RequireCurrent(ResourceOperationLease root, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); cancellation.ThrowIfCancellationRequested();
        if (!root.IsActive || root.OperationId != _operationId || PhysicalObjectIdentity.Read(_input.SafeFileHandle) != _identity ||
            PhysicalObjectIdentity.Read(ArchivePath) != _identity || PhysicalObjectIdentity.Read(Path.GetDirectoryName(ArchivePath)!) != _parentIdentity ||
            _input.Length != Release.Artifact.SizeBytes || _feeds.RequireRelease(root, _feed, Release.HostVersion).Release != Release) throw Invalid();
        PhysicalObjectIdentity.RequireSingleLink(ArchivePath);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            try { _archive.Dispose(); } finally { _input.Dispose(); _parent.Dispose(); }
        }
    }

    private static IOException Invalid() => new("update_candidate_unbound");
}
