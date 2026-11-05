using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionWorkInventory
{
    internal static HashSet<int> Removed(FrozenFileCleanup plan, JsonObject journal, IEnumerable<int> previous)
    {
        var result = previous.ToHashSet();
        var steps = FileCleanupItems.Steps(plan);
        foreach (var receipt in journal["receipts"]!.AsArray())
            if (receipt!["effect"]!.GetValue<string>() == "completed")
            {
                var index = steps.ToList().FindIndex(value => value == receipt["stepId"]!.GetValue<string>());
                if (index >= 0) result.Add(plan.ExtensionWork!.Page.Indices[index]);
            }
        return result;
    }

    internal static void Require(BoundExtensionWork origin, IReadOnlySet<int> removed, WorkTreeProtection? protection = null)
    {
        var proof = origin.Proof;
        if (PhysicalObjectIdentity.Read(Path.GetDirectoryName(proof.WorkspacePath)!) != proof.ParentIdentity) throw Invalid();
        var expected = proof.Nodes.Where(node => !removed.Contains(node.Index)).ToDictionary(node => node.RelativePath, StringComparer.Ordinal);
        foreach (var node in proof.Nodes.Where(node => removed.Contains(node.Index)))
            if (Exists(WorkNodeDerivation.Resolve(proof, node))) throw Invalid();
        if (expected.Count == 0)
        {
            if (Exists(proof.WorkspacePath)) throw Invalid();
            return;
        }
        if (!expected.TryGetValue("", out var root) || root.Kind != FileObjectKind.Directory) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(proof.WorkspacePath);
        while (pending.TryPop(out var path))
        {
            if (seen.Count >= WorkNodeDerivation.MaximumNodes) throw Invalid();
            var relative = Path.GetRelativePath(proof.WorkspacePath, path).Replace('\\', '/');
            if (relative == ".") relative = "";
            PhysicalPathPolicy.NormalizeWithoutLinks(path);
            if (!seen.Add(relative) || !expected.TryGetValue(relative, out var node)) throw Invalid();
            Verify(origin, node, protection);
            if (node.Kind == FileObjectKind.Directory)
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    if (seen.Count + pending.Count >= WorkNodeDerivation.MaximumNodes) throw Invalid();
                    pending.Push(child);
                }
        }
        if (seen.Count != expected.Count) throw Invalid();
    }

    internal static void Verify(BoundExtensionWork origin, ExtensionWorkNode node, WorkTreeProtection? protection = null)
    {
        var path = WorkNodeDerivation.Resolve(origin.Proof, node);
        if (PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!) != node.ParentIdentity ||
            PhysicalObjectIdentity.Read(path) != node.Identity) throw Invalid();
        var attributes = File.GetAttributes(path);
        if (((attributes & FileAttributes.Directory) != 0) != (node.Kind == FileObjectKind.Directory)) throw Invalid();
        if (node.Kind == FileObjectKind.File)
        {
            PhysicalObjectIdentity.RequireSingleLink(path);
            if (protection?.Holds(node) == true) return;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != node.Identity || input.Length != node.SizeBytes ||
                Convert.ToHexStringLower(SHA256.HashData(input)) != node.Sha256) throw Invalid();
        }
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static IOException Invalid() => new("extension_work_tree_changed");
}
