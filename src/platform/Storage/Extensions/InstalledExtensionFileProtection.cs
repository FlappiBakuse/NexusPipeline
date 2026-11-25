using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledExtensionFileProtection : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (InstalledDirectoryMoveHandle Root, TreeReadLease Tree)> _held = new(StringComparer.Ordinal);
    private bool _disposed;

    internal void Protect(string id, string target, PhysicalObjectIdentity parent, ArchivedFileTree tree)
    {
        if (tree.Snapshot.Shape != FileObjectKind.Directory) throw new IOException("extension_installation_tree_missing");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_held.ContainsKey(id)) throw new IOException("extension_directory_already_protected");
            var move = InstalledDirectoryMoveHandle.Acquire(target, tree.OriginalIdentity!);
            try { _held.Add(id, (move, TreeReadLease.AcquireMetadata(target, parent, tree))); }
            catch { move.Dispose(); throw; }
        }
    }

    internal void ProtectExisting(BoundExtensionInstallation installed)
    {
        var footprint = installed.Footprint;
        if (!installed.Metadata.IsInstalled || ExtensionFileReconciliation.Observe(installed) is not null) return;
        lock (_gate)
        {
            if (_disposed || _held.ContainsKey(footprint.ExtensionId)) return;
            var root = InstalledDirectoryMoveHandle.Acquire(footprint.TargetPath, footprint.Payload.OriginalIdentity!);
            try
            {
                TreeReadLease tree;
                try { tree = TreeReadLease.AcquireMetadata(footprint.TargetPath, footprint.ParentIdentity, footprint.Payload); }
                catch (IOException) when (Directory.Exists(footprint.TargetPath))
                {
                    // Keep the surviving original objects protected while the installation is marked damaged.
                    tree = TreeReadLease.Capture(footprint.TargetPath, footprint.ParentIdentity, new(new(4096, 268435456)),
                        CancellationToken.None, "extension.protect.damaged", out _);
                }
                _held.Add(footprint.ExtensionId, (root, tree));
            }
            catch { root.Dispose(); throw; }
        }
    }

    internal InstalledDirectoryMoveHandle? TakeForMove(string id)
    {
        lock (_gate)
        {
            if (!_held.Remove(id, out var held)) return null;
            held.Tree.Dispose();
            return held.Root;
        }
    }

    internal void Release(string id) => TakeForMove(id)?.Dispose();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var item in _held.Values) { item.Tree.Dispose(); item.Root.Dispose(); }
            _held.Clear();
        }
    }
}
