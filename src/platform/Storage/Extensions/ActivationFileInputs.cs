using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class ActivationFileInputs : IDisposable
{
    private readonly IDisposable _staged;
    private readonly TreeReadLease _current;
    private readonly string _target;
    private readonly ArchivedFileTree _tree;
    private readonly BlobReference _archive;
    private int _disposed;
    internal PhysicalObjectIdentity ParentIdentity { get; }

    internal ActivationFileInputs(IDisposable staged, string target, PhysicalObjectIdentity parent,
        ArchivedFileTree tree, BlobReference archive)
    {
        _staged = staged; _target = target; _tree = tree; _archive = archive; ParentIdentity = parent;
        _current = staged as TreeReadLease ?? TreeReadLease.Acquire(target, parent, tree);
    }

    internal TreeReadLease.TreeBorrow Borrow()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _current.Borrow();
    }

    internal ReadProviderPackage ReadPackage(RuntimeInstance instance, string extensionId, byte[] manifestBytes,
        ContractReader contracts, List<IDisposable> held)
    {
        var verifiedArchive = new ImmutableBlobStore(instance, contracts).OpenVerified(_archive);
        held.Add(verifiedArchive);
        held.Add(Borrow());
        var archivePath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, _archive.RelativePath));
        var relative = Path.GetRelativePath(_target, archivePath);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative)) throw new IOException("execution_provider_archive_inside_package");
        held.Add(StableDirectoryLease.Acquire(Path.GetDirectoryName(archivePath)!));
        var input = ProviderPackageReader.OpenRead(archivePath, held);
        var identity = PhysicalObjectIdentity.Read(input.SafeFileHandle);
        if (input.Length != _archive.SizeBytes || verifiedArchive.Length != _archive.SizeBytes ||
            PhysicalObjectIdentity.Read(verifiedArchive.SafeFileHandle) != identity)
            throw new IOException("execution_provider_package_changed");
        var archive = new VerifiedPackageFile(Path.GetFileName(archivePath), input.Length, _archive.Sha256, identity,
            PhysicalObjectIdentity.Read(Path.GetDirectoryName(archivePath)!));
        var identities = _tree.OriginalObjects.ToDictionary(item => item.RelativePath, item => item.Identity, StringComparer.Ordinal);
        var files = ImmutableArray.CreateBuilder<VerifiedPackageFile>();
        foreach (var entry in _tree.Snapshot.Entries.Where(item => item.Kind == FileObjectKind.File)
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_target, entry.RelativePath));
            PhysicalObjectIdentity.RequireSingleLink(path);
            files.Add(new(entry.RelativePath, entry.SizeBytes, entry.Sha256 ?? throw new IOException("execution_provider_file_unbound"),
                identities[entry.RelativePath], PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!)));
        }
        var manifest = files.SingleOrDefault(item => item.RelativePath == "manifest.json")
            ?? throw new IOException("execution_provider_manifest_missing");
        if (manifest.SizeBytes != manifestBytes.LongLength || manifest.Sha256 != Convert.ToHexStringLower(SHA256.HashData(manifestBytes)))
            throw new IOException("execution_provider_manifest_changed");
        var admitted = new ExtensionManifestAdmission(contracts).ReadCurrent(manifestBytes, extensionId);
        return new(_target, archivePath, admitted, manifestBytes.ToArray(), files.ToImmutable(), archive);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _current.Dispose(); }
        finally { _staged.Dispose(); }
    }
}
