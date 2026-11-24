using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage;

[SupportedOSPlatform("windows")]
internal sealed class OwnedDownloadStaging : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly PhysicalDirectoryHandle _directory;
    private readonly StableDirectoryLease _parents;
    private readonly ResourceOperationLease _root;
    private readonly PhysicalObjectIdentity _parentIdentity, _identity, _stagingIdentity;
    private readonly long _sizeBytes;
    private readonly string _sha256, _invalidCode;
    private FileStream? _stream;
    private bool _disposed, _transferred, _stagingRemoved;
    internal string ArchivePath { get; }

    private OwnedDownloadStaging(RuntimeInstance instance, InstanceLease lease, PhysicalDirectoryHandle directory,
        StableDirectoryLease parents, ResourceOperationLease root, string path, FileStream stream,
        long sizeBytes, string sha256, string invalidCode)
    {
        _instance = instance; _lease = lease; _directory = directory; _parents = parents; _root = root;
        ArchivePath = path; _stream = stream; _sizeBytes = sizeBytes; _sha256 = sha256; _invalidCode = invalidCode;
        _parentIdentity = PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!);
        _stagingIdentity = PhysicalObjectIdentity.Read(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!);
        _identity = PhysicalObjectIdentity.Read(stream.SafeFileHandle);
    }

    internal static async Task<OwnedDownloadStaging> Receive(RuntimeInstance instance, InstanceLease lease,
        ResourceLeaseManager resources, string name, long sizeBytes, string sha256, string invalidCode,
        string cleanupCode, Func<Stream, CancellationToken, Task> transfer, CancellationToken cancellation)
    {
        lease.VerifyHeld(instance); cancellation.ThrowIfCancellationRequested();
        using var runtime = PhysicalDirectoryHandle.Open(instance.RuntimeRoot);
        using var staging = runtime.AcquireDirectory("staging");
        StableDirectoryLease? parents = null; ResourceOperationLease? root = null; FileStream? stream = null;
        PhysicalDirectoryHandle? directory = null; OwnedDownloadStaging? owned = null;
        try
        {
            var stagingPath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "staging"));
            parents = StableDirectoryLease.Acquire(stagingPath);
            var parent = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(stagingPath, name));
            root = resources.TryAcquire(Guid.NewGuid(), PhysicalResourceAdmission.Read(parent)).Lease ?? throw new IOException(invalidCode);
            directory = staging.CreateNewDirectory(name);
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(parent, "payload.zip"));
            stream = new FileStream(directory.CreateNewFile(Path.GetFileName(path)), FileAccess.ReadWrite);
            owned = new(instance, lease, directory, parents, root, path, stream, sizeBytes, sha256, invalidCode);
            await transfer(stream, cancellation).ConfigureAwait(false);
            lease.VerifyHeld(instance); cancellation.ThrowIfCancellationRequested();
            stream.Flush(flushToDisk: true); stream.Position = 0;
            if (stream.Length != sizeBytes || Convert.ToHexStringLower(SHA256.HashData(stream)) != sha256) throw owned.Invalid();
            var identity = PhysicalObjectIdentity.Read(stream.SafeFileHandle);
            stream.Dispose(); owned._stream = null;
            owned._stream = new FileStream(directory.OpenFile(Path.GetFileName(path), write: false), FileAccess.Read);
            if (PhysicalObjectIdentity.Read(owned._stream.SafeFileHandle) != identity ||
                PhysicalObjectIdentity.Read(path) != identity || identity != owned._identity) throw owned.Invalid();
            PhysicalObjectIdentity.RequireSingleLink(path);
            return owned;
        }
        catch (Exception failure)
        {
            if (owned is not null)
            {
                try { owned.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException(cleanupCode, failure, cleanup); }
            }
            else
            {
                var cleanup = new CleanupFailures();
                cleanup.Attempt(() => stream?.Dispose());
                cleanup.Attempt(() => directory?.Dispose());
                cleanup.Attempt(() => root?.Dispose());
                cleanup.Attempt(() => parents?.Dispose());
                cleanup.Preserve(failure);
            }
            throw;
        }
    }

    internal void RequireOwned()
    {
        _lease.VerifyHeld(_instance);
        if (_disposed || _transferred || _stream is null || !_root.IsActive ||
            PhysicalObjectIdentity.Read(_stream.SafeFileHandle) != _identity || _stream.Length != _sizeBytes) throw Invalid();
    }

    internal FileStream OpenRead()
    {
        RequireOwned();
        var input = new FileStream(_directory.OpenReadFile(Path.GetFileName(ArchivePath)), FileAccess.Read);
        if (PhysicalObjectIdentity.Read(input.SafeFileHandle) == _identity) return input;
        input.Dispose(); throw Invalid();
    }

    internal bool TryTransferTo(string destination, CancellationToken cancellation)
    {
        RequireOwned(); cancellation.ThrowIfCancellationRequested();
        _stream!.Dispose(); _stream = null;
        _stream = new FileStream(_directory.OpenOwnedFile(Path.GetFileName(ArchivePath)), FileAccess.Read);
        if (PhysicalObjectIdentity.Read(_stream.SafeFileHandle) != _identity || _stream.Length != _sizeBytes ||
            Convert.ToHexStringLower(SHA256.HashData(_stream)) != _sha256) throw Invalid();
        try
        {
            PhysicalObjectMove.MoveVerified(new(ArchivePath, _parentIdentity, _identity, destination,
                PhysicalObjectIdentity.Read(Path.GetDirectoryName(destination)!), FileObjectKind.File), _stream.SafeFileHandle);
        }
        catch (IOException error) when (error.Message == "physical_move_rename_rejected" &&
            error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 80 or 183 })
        {
            if (PhysicalObjectIdentity.Read(ArchivePath) != _identity) throw;
            return false;
        }
        _transferred = true;
        _stream.Dispose(); _stream = null;
        return true;
    }

    internal void CompleteTransfer()
    {
        if (!_transferred) throw Invalid();
        RemoveDirectory();
    }

    private void RemoveDirectory()
    {
        if (_stagingRemoved) return;
        _directory.Dispose();
        PhysicalObjectDeletion.DeleteVerified(new(Path.GetDirectoryName(ArchivePath)!, _stagingIdentity,
            _parentIdentity, FileObjectKind.Directory, 0, null));
        _stagingRemoved = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_transferred) { _stream?.Dispose(); _stream = null; RemoveDirectory(); return; }
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
            RemoveDirectory();
        }
        finally { _directory.Dispose(); _parents.Dispose(); _root.Dispose(); }
    }

    private IOException Invalid() => new(_invalidCode);
}
