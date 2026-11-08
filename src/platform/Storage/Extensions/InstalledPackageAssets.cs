using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Assets;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledPackageAssets(PreparedExtensionActivation source, ExtensionScope scope,
    InstalledExtensionConsumption consumption, string root, TreeReadLease.TreeBorrow files, ArchivedFileTree original)
    : IExtensionPackageAssets
{
    internal IDisposable? Enter()
    {
        scope.Cancellation.ThrowIfCancellationRequested();
        if (!scope.Activation.IsAccepting) throw new IOException("extension_activation_revoked");
        if (scope.IsPublished) return consumption.Begin();
        source.RequireBeforeCallback();
        return null;
    }

    public async ValueTask<IExtensionPackageReadLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var access = Enter();
        var held = files.Borrow();
        PackageLease? result = null;
        try
        {
            PhysicalTreeIdentity.RequireSingleLinks(root, original.Snapshot);
            result = new(this, root, original, held);
            await scope.OwnAsync(result).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); scope.Cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch { if (result is not null) await result.DisposeAsync(); else held.Dispose(); throw; }
    }

    private sealed class PackageLease(InstalledPackageAssets owner, string root,
        ArchivedFileTree original, IDisposable held) : IExtensionPackageReadLease
    {
        private readonly object _gate = new();
        private readonly List<FileStream> _streams = [];
        private bool _disposed;
        public NexusPipeline.Extensibility.Assets.ExtensionPackageFile File(string portableRelativePath)
        {
            using var access = owner.Enter();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var entry = Entry(portableRelativePath, FileObjectKind.File);
                return new(Resolve(entry.RelativePath), entry.SizeBytes, entry.Sha256!);
            }
        }

        public string Directory(string portableRelativePath)
        {
            using var access = owner.Enter();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return Resolve(Entry(portableRelativePath, FileObjectKind.Directory).RelativePath);
            }
        }

        public Stream OpenRead(string portableRelativePath)
        {
            using var access = owner.Enter();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var entry = Entry(portableRelativePath, FileObjectKind.File);
                if (_streams.Count >= 256) throw new IOException("extension_asset_open_limit");
                var input = new FileStream(Resolve(entry.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    if (input.Length != entry.SizeBytes ||
                        Convert.ToHexStringLower(SHA256.HashData(input)) != entry.Sha256 ||
                        PhysicalObjectIdentity.Read(input.SafeFileHandle) != original.OriginalObjects
                            .Single(item => item.RelativePath == entry.RelativePath).Identity)
                        throw new IOException("extension_asset_changed");
                    input.Position = 0; _streams.Add(input); return input;
                }
                catch { input.Dispose(); throw; }
            }
        }

        private FileTreeEntry Entry(string relative, FileObjectKind kind)
        {
            if (relative.Length != 0) PhysicalPathPolicy.ValidateRelativePath(relative);
            return original.Snapshot.Entries.SingleOrDefault(item => item.RelativePath == relative && item.Kind == kind)
                ?? throw new IOException("extension_asset_not_owned");
        }

        private string Resolve(string relative)
        {
            var path = relative.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, relative));
            if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path ||
                Path.GetRelativePath(root, path).Replace('\\', '/') != (relative.Length == 0 ? "." : relative))
                throw new IOException("extension_asset_not_owned");
            return path;
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                try { foreach (var stream in _streams) stream.Dispose(); }
                finally { held.Dispose(); }
                _streams.Clear();
            }
            return ValueTask.CompletedTask;
        }
    }
}
