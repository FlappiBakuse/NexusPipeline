using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class WorkTreeProtection : IDisposable
{
    private readonly Dictionary<int, (ExtensionWorkNode Node, FileStream Input)> _files = [];
    private readonly StableDirectoryLease _parent;

    private WorkTreeProtection(string parent) => _parent = StableDirectoryLease.Acquire(parent);

    internal static WorkTreeProtection Acquire(BoundExtensionWork origin, IReadOnlySet<int> removed)
    {
        var result = new WorkTreeProtection(Path.GetDirectoryName(origin.Proof.WorkspacePath)!);
        try
        {
            foreach (var node in origin.Proof.Nodes.Where(node => node.Kind == FileObjectKind.File && !removed.Contains(node.Index)))
            {
                var path = WorkNodeDerivation.Resolve(origin.Proof, node);
                PhysicalObjectIdentity.RequireSingleLink(path);
                var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                try
                {
                    if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != node.Identity || input.Length != node.SizeBytes ||
                        Convert.ToHexStringLower(SHA256.HashData(input)) != node.Sha256)
                        throw new IOException("extension_work_tree_changed");
                    result._files.Add(node.Index, (node, input));
                }
                catch { input.Dispose(); throw; }
            }
            ExtensionWorkInventory.Require(origin, removed, result);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal bool Holds(ExtensionWorkNode node) => _files.TryGetValue(node.Index, out var held) &&
        held.Node == node && PhysicalObjectIdentity.Read(held.Input.SafeFileHandle) == node.Identity && held.Input.Length == node.SizeBytes;

    internal void Removed(int index)
    {
        if (_files.Remove(index, out var held)) held.Input.Dispose();
    }

    public void Dispose()
    {
        foreach (var held in _files.Values) held.Input.Dispose();
        _files.Clear(); _parent.Dispose();
    }
}
