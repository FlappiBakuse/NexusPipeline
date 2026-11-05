using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage;

internal sealed class PhysicalResourceAdmission : IDisposable
{
    private readonly List<IDisposable> _parents = [];
    internal ImmutableArray<ResourceClaim> Claims { get; private set; }

    internal static PhysicalResourceAdmission Prepare(IEnumerable<string> targets)
    {
        var prepared = new PhysicalResourceAdmission();
        try
        {
            var all = new List<ResourceClaim>();
            foreach (var target in targets)
            {
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(target);
                if (path != target || Path.GetDirectoryName(path) is not { } parent) throw new IOException("configuration_edit_target_invalid");
                prepared._parents.Add(StableDirectoryLease.Acquire(parent));
                all.AddRange(Read(path));
            }
            prepared.Claims = Merge(all);
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }

    internal static ImmutableArray<ResourceClaim> Read(string target, ResourceClaimMode targetMode = ResourceClaimMode.Mutation,
        bool includeDescendants = true)
    {
        if (!Enum.IsDefined(targetMode)) throw new ArgumentException("physical_resource_claim_mode_invalid");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(target);
        if (path != target || Path.GetDirectoryName(path) is null) throw new IOException("configuration_edit_target_invalid");
        var claims = new List<ResourceClaim>();
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var mode = current == path ? targetMode : ResourceClaimMode.Reference;
            var exists = Directory.Exists(current) || File.Exists(current);
            if (current == path && targetMode == ResourceClaimMode.Mutation && File.Exists(current)) PhysicalObjectIdentity.RequireSingleLink(current);
            if (Path.GetDirectoryName(current) is { } parent)
            {
                var identity = PhysicalObjectIdentity.Read(parent);
                var entryName = exists ? PhysicalObjectIdentity.EntryName(current) : Path.GetFileName(current);
                claims.Add(new(EntryKey(identity, entryName), mode));
            }
            if (exists)
            {
                var identity = PhysicalObjectIdentity.Read(current);
                claims.Add(new($"filesystem:object:{identity.VolumeSerial}:{identity.FileId}", mode));
            }
            if (claims.Count > 4096) throw new IOException("configuration_file_claim_limit");
        }
        if (includeDescendants && Directory.Exists(path)) AddDescendants(path, claims, targetMode);
        return Merge(claims);
    }

    private static void AddDescendants(string root, List<ResourceClaim> claims, ResourceClaimMode mode)
    {
        var directories = new Stack<string>(); directories.Push(root); var count = 1;
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > SnapshotBudget.Default.MaximumEntries) throw new IOException("configuration_file_claim_limit");
                PhysicalPathPolicy.NormalizeWithoutLinks(entry);
                if (mode == ResourceClaimMode.Mutation && File.Exists(entry)) PhysicalObjectIdentity.RequireSingleLink(entry);
                var identity = PhysicalObjectIdentity.Read(entry);
                claims.Add(new($"filesystem:object:{identity.VolumeSerial}:{identity.FileId}", mode));
                if (claims.Count > 4096) throw new IOException("configuration_file_claim_limit");
                if (Directory.Exists(entry)) directories.Push(entry);
            }
        }
    }

    internal static string EntryKey(PhysicalObjectIdentity parent, string entryName)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entryName.ToUpperInvariant())));
        return $"filesystem:entry:{parent.VolumeSerial}:{parent.FileId}:{name}";
    }

    internal static ImmutableArray<ResourceClaim> ReadTree(string root, FileTreeSnapshot tree)
    {
        tree = FileTreePolicy.Freeze(tree);
        var observed = PhysicalTreeIdentity.Capture(root, tree).ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var claims = Read(root, ResourceClaimMode.Reference, includeDescendants: false).ToList();
        foreach (var item in observed.Values.Where(item => item.RelativePath.Length != 0))
        {
            var split = item.RelativePath.LastIndexOf('/'); var parent = split < 0 ? "" : item.RelativePath[..split];
            if (!observed.TryGetValue(parent, out var directory)) throw new IOException("physical_tree_parent_missing");
            claims.Add(new(EntryKey(directory.Identity, item.RelativePath[(split + 1)..]), ResourceClaimMode.Reference));
            claims.Add(new($"filesystem:object:{item.Identity.VolumeSerial}:{item.Identity.FileId}", ResourceClaimMode.Reference));
        }
        return Merge(claims);
    }

    internal static ImmutableArray<ResourceClaim> Merge(IEnumerable<ResourceClaim> claims) => claims.GroupBy(item => item.ResourceKey, StringComparer.Ordinal)
        .Select(group => new ResourceClaim(group.Key, group.Any(item => item.Mode == ResourceClaimMode.Mutation) ? ResourceClaimMode.Mutation : ResourceClaimMode.Reference))
        .OrderBy(item => item.ResourceKey, StringComparer.Ordinal).ToImmutableArray();

    public void Dispose()
    {
        for (var index = _parents.Count - 1; index >= 0; index--) _parents[index].Dispose();
        _parents.Clear();
    }
}
