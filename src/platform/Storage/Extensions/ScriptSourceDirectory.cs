using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ScriptSourceDirectory : IDisposable
{
    private sealed record Entry(string Name, string Kind, PhysicalObjectIdentity Identity);
    private readonly string root;
    private readonly PhysicalObjectIdentity identity;
    private readonly ResourceLeaseManager resources;
    private readonly Action current;
    private readonly CancellationToken cancellation;
    private readonly Dictionary<string, Entry[]> directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
    private readonly List<IDisposable> held = [];
    private int entries;
    private long bytes;
    private bool disposed;

    internal string Root => root;

    internal ScriptSourceDirectory(string path, ResourceLeaseManager resources, Action current, CancellationToken cancellation)
    {
        this.resources = resources; this.current = current; this.cancellation = cancellation;
        current(); cancellation.ThrowIfCancellationRequested();
        root = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        if (!Directory.Exists(root)) throw new IOException("script_source_root_missing");
        identity = PhysicalObjectIdentity.Read(root);
        try { Hold(root); held.Add(StableDirectoryLease.Acquire(root)); RequireCurrent(); }
        catch { Dispose(); throw; }
    }

    internal string List(string relative)
    {
        Check();
        var rows = Entries(relative);
        return JsonSerializer.Serialize(rows.Select(row => new { name = row.Name, kind = row.Kind }));
    }

    internal string Read(string relative)
    {
        Check(); RequireFile(relative);
        if (files.TryGetValue(relative, out var cached)) return cached;
        if (files.Count >= 128) throw Limit();
        var path = Resolve(relative); Hold(path);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length is < 1 or > 8 * 1024 * 1024 || bytes + input.Length > 32 * 1024 * 1024) throw Limit();
            var entry = Find(relative)!;
            if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != entry.Identity) throw Changed();
            var buffer = new byte[checked((int)input.Length)]; input.ReadExactly(buffer);
            var value = new UTF8Encoding(false, true).GetString(buffer);
            cancellation.ThrowIfCancellationRequested(); bytes += input.Length;
            held.Add(input); files.Add(relative, value); return value;
        }
        catch { input.Dispose(); throw; }
    }

    internal void RequireFile(string relative)
    {
        Check(); PhysicalPathPolicy.ValidateRelativePath(relative);
        if (Find(relative) is not { Kind: "file" }) throw new IOException("script_source_file_missing");
    }

    internal void RequirePath(string relative)
    {
        Check(); PhysicalPathPolicy.ValidateRelativePath(relative);
        if (Find(relative) is null) throw new IOException("script_source_path_missing");
    }

    private Entry? Find(string relative)
    {
        var slash = relative.LastIndexOf('/'); var parent = slash < 0 ? "" : relative[..slash];
        var name = relative[(slash + 1)..];
        var entry = Entries(parent).SingleOrDefault(row => row.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is not null && entry.Name != name) throw Changed();
        return entry;
    }

    private Entry[] Entries(string relative)
    {
        if (relative.Length != 0) PhysicalPathPolicy.ValidateRelativePath(relative);
        if (directories.TryGetValue(relative, out var cached)) return cached;
        if (directories.Count >= 128) throw Limit();
        if (relative.Length != 0 && Find(relative) is { } parent && parent.Kind != "directory") throw Changed();
        var path = Resolve(relative); Hold(path);
        if (Directory.Exists(path)) held.Add(StableDirectoryLease.Acquire(path));
        var observed = Observe(path);
        entries += observed.Length;
        if (entries > 4096 || directories.Count >= 128) throw Limit();
        directories.Add(relative, observed); return observed;
    }

    private Entry[] Observe(string path)
    {
        if (!Directory.Exists(path)) return [];
        var result = new List<Entry>();
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            cancellation.ThrowIfCancellationRequested();
            if (result.Count >= 4096) throw Limit();
            var actual = PhysicalPathPolicy.NormalizeWithoutLinks(child);
            result.Add(new(Path.GetFileName(actual), Directory.Exists(actual) ? "directory" : "file", PhysicalObjectIdentity.Read(actual)));
        }
        return result.OrderBy(row => row.Name, StringComparer.Ordinal).ToArray();
    }

    private string Resolve(string relative) => relative.Length == 0 ? root :
        PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private void Hold(string path) => held.Add(ProjectReadLease.Acquire(resources,
        PhysicalResourceAdmission.Read(path, ResourceClaimMode.Reference, includeDescendants: false)));

    private void Check()
    {
        ObjectDisposedException.ThrowIf(disposed, this); cancellation.ThrowIfCancellationRequested(); current();
        if (PhysicalObjectIdentity.Read(root) != identity) throw Changed();
    }

    internal void RequireCurrent()
    {
        Check();
        foreach (var row in directories)
            if (!row.Value.SequenceEqual(Observe(Resolve(row.Key)))) throw Changed();
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
    private static IOException Changed() => new("script_source_changed");
    private static IOException Limit() => new("script_source_limit");
}
