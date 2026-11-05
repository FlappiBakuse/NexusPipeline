using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class OwnedExtensionDownload : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly PhysicalDirectoryHandle _directory;
    private readonly StableDirectoryLease _parents;
    private readonly ResourceOperationLease _root;
    private readonly PhysicalObjectIdentity _parentIdentity, _identity, _stagingIdentity;
    private FileStream? _stream;
    private bool _disposed;
    internal string ArchivePath { get; }

    private OwnedExtensionDownload(RuntimeInstance instance, InstanceLease lease, PhysicalDirectoryHandle directory,
        StableDirectoryLease parents, ResourceOperationLease root, string path, FileStream stream)
    {
        _instance = instance; _lease = lease; _directory = directory; _parents = parents; _root = root;
        ArchivePath = path; _stream = stream;
        _parentIdentity = PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!);
        _stagingIdentity = PhysicalObjectIdentity.Read(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!);
        _identity = PhysicalObjectIdentity.Read(stream.SafeFileHandle);
    }

    internal static async Task<OwnedExtensionDownload> Receive(RuntimeInstance instance, InstanceLease lease,
        ResourceLeaseManager resources, Guid operationId, ExtensionCatalogEntry entry,
        Func<Stream, CancellationToken, Task> transfer, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (operationId == Guid.Empty || entry.ArtifactBytes is < 1 or > StoreResponseReader.MaximumArtifactBytes ||
            entry.ArtifactSha256.Length != 64 || entry.ArtifactSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Invalid();
        lease.VerifyHeld(instance);
        cancellation.ThrowIfCancellationRequested();
        using var runtime = PhysicalDirectoryHandle.Open(instance.RuntimeRoot);
        using var staging = runtime.AcquireDirectory("staging");
        StableDirectoryLease? parents = null; ResourceOperationLease? root = null; FileStream? stream = null;
        PhysicalDirectoryHandle? directory = null;
        OwnedExtensionDownload? owned = null;
        try
        {
            var stagingPath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "staging"));
            parents = StableDirectoryLease.Acquire(stagingPath);
            var name = "extension-" + operationId.ToString("N");
            var parent = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(stagingPath, name));
            root = resources.TryAcquire(Guid.NewGuid(), PhysicalResourceAdmission.Read(parent)).Lease ?? throw Invalid();
            directory = staging.CreateNewDirectory(name);
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(parent, "payload.zip"));
            stream = new FileStream(directory.CreateNewFile(Path.GetFileName(path)), FileAccess.ReadWrite);
            owned = new(instance, lease, directory, parents, root, path, stream);
            await transfer(stream, cancellation).ConfigureAwait(false);
            lease.VerifyHeld(instance); cancellation.ThrowIfCancellationRequested();
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            if (stream.Length != entry.ArtifactBytes || Convert.ToHexStringLower(SHA256.HashData(stream)) != entry.ArtifactSha256)
                throw Invalid();
            var identity = PhysicalObjectIdentity.Read(stream.SafeFileHandle);
            stream.Dispose(); owned._stream = null;
            owned._stream = new FileStream(directory.OpenFile(Path.GetFileName(path), write: false), FileAccess.Read);
            if (PhysicalObjectIdentity.Read(owned._stream.SafeFileHandle) != identity ||
                PhysicalObjectIdentity.Read(path) != identity || identity != owned._identity) throw Invalid();
            PhysicalObjectIdentity.RequireSingleLink(path);
            return owned;
        }
        catch (Exception failure)
        {
            if (owned is not null)
            {
                try { owned.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("extension_download_cleanup_pending", failure, cleanup); }
            }
            else
            {
                stream?.Dispose(); directory?.Dispose(); root?.Dispose(); parents?.Dispose();
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            FileObjectDeletion intent;
            try
            {
                _lease.VerifyHeld(_instance);
                if (!_root.IsActive || _stream is not { } stream ||
                    PhysicalObjectIdentity.Read(stream.SafeFileHandle) != _identity ||
                    PhysicalObjectIdentity.Read(ArchivePath) != _identity ||
                    PhysicalObjectIdentity.Read(Path.GetDirectoryName(ArchivePath)!) != _parentIdentity) throw Invalid();
                stream.Flush(); stream.Position = 0;
                intent = new(ArchivePath, _parentIdentity, _identity, FileObjectKind.File,
                    stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
            }
            finally { _stream?.Dispose(); _stream = null; }
            PhysicalObjectDeletion.DeleteVerified(intent);
            _directory.Dispose();
            PhysicalObjectDeletion.DeleteVerified(new(Path.GetDirectoryName(ArchivePath)!, _stagingIdentity,
                _parentIdentity, FileObjectKind.Directory, 0, null));
        }
        finally { _directory.Dispose(); _parents.Dispose(); _root.Dispose(); }
    }

    internal FileStream OpenRead()
    {
        _lease.VerifyHeld(_instance);
        if (_disposed || _stream is null || !_root.IsActive) throw Invalid();
        var input = new FileStream(_directory.OpenFile(Path.GetFileName(ArchivePath), write: false), FileAccess.Read);
        if (PhysicalObjectIdentity.Read(input.SafeFileHandle) == _identity) return input;
        input.Dispose(); throw Invalid();
    }

    private static IOException Invalid() => new("extension_download_staging_unbound");
}
