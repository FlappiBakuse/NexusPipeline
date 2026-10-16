using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateOwnedEntry(string Path, bool IsDirectory, long SizeBytes, string? Sha256);

internal sealed record UpdateInventory(IReadOnlyList<UpdateOwnedEntry> Entries)
{
    public bool Equals(UpdateInventory? other) => other is not null && Entries.SequenceEqual(other.Entries);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in Entries) hash.Add(entry);
        return hash.ToHashCode();
    }

    internal void Validate()
    {
        if (Entries is null || Entries.Count > 8192) throw new InvalidDataException("update.inventory_limit");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = Entries.Where(entry => entry is { IsDirectory: true }).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (entry is null || !ApplicationPayload.SafePath(entry.Path) || !paths.Add(entry.Path)
                || entry.IsDirectory && (entry.SizeBytes != 0 || entry.Sha256 is not null)
                || !entry.IsDirectory && (entry.SizeBytes is < 0 or > 256L * 1024 * 1024 || !Hash(entry.Sha256)))
                throw new InvalidDataException("update.inventory_entry");
            int slash = entry.Path.LastIndexOf('/');
            if (slash >= 0 && !directories.Contains(entry.Path[..slash]))
                throw new InvalidDataException("update.inventory_parent");
        }
    }

    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    internal static UpdateInventory Capture(string root, params string[] scopes)
    {
        PayloadPathSafety.RequireLinkFree(root);
        var entries = new Dictionary<string, UpdateOwnedEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(string relative)
        {
            if (entries.ContainsKey(relative)) return;
            string full = Resolve(root, relative);
            if (!File.Exists(full) && !Directory.Exists(full)) return;
            if (relative.Count(ch => ch == '/') > 32 || entries.Count >= 8192)
                throw new InvalidDataException("update.inventory_limit");
            for (int slash = relative.LastIndexOf('/'); slash >= 0; slash = relative.LastIndexOf('/', slash - 1))
                entries.TryAdd(relative[..slash], new(relative[..slash], true, 0, null));
            bool directory = Directory.Exists(full);
            if (directory)
            {
                entries.Add(relative, new(relative, true, 0, null));
                foreach (string child in Directory.EnumerateFileSystemEntries(full)) Add(relative + '/' + Path.GetFileName(child));
            }
            else
            {
                using var stream = File.OpenRead(full);
                entries.Add(relative, new(relative, false, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream))));
            }
        }
        foreach (string scope in scopes.Length == 0 ? Directory.EnumerateFileSystemEntries(root).Select(path => Path.GetFileName(path)) : scopes)
            Add(scope);
        var result = new UpdateInventory(entries.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray());
        result.Validate();
        return result;
    }

    internal UpdateInventory WithFile(string path, byte[] bytes)
    {
        var result = new UpdateInventory(Entries.Append(new(path, false, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray());
        result.Validate();
        return result;
    }

    internal static string Resolve(string root, string relative)
    {
        if (!ApplicationPayload.SafePath(relative)) throw new InvalidDataException("update.inventory_path");
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        PayloadPathSafety.RequireLinkFree(path);
        return path;
    }

    internal void RequireOwned(string root, string? scope = null)
    {
        foreach (var actual in Current(root, scope).Entries)
            if (!Entries.Contains(actual)) throw new IOException("update.unowned_content: " + actual.Path);
    }

    internal static UpdateInventory Current(string root, string? scope)
    {
        if (scope is not null) return Capture(root, scope);
        if (!Directory.Exists(root))
        {
            if (File.Exists(root)) throw new IOException("update.directory_replaced");
            PayloadPathSafety.RequireLinkFree(root);
            return new([]);
        }
        return Capture(root);
    }

    internal void DeleteOwned(string root, Action guard, string? scope = null)
    {
        RequireOwned(root, scope);
        foreach (var entry in Entries.Where(entry => !entry.IsDirectory
            && (scope is null || entry.Path == scope || entry.Path.StartsWith(scope + '/', StringComparison.Ordinal))))
        {
            guard();
            string path = Resolve(root, entry.Path);
            if (Directory.Exists(path)) throw new IOException("update.file_replaced");
            if (File.Exists(path)) VerifiedFileDeletion.Delete(path, entry.SizeBytes, entry.Sha256!);
        }
        foreach (var entry in Entries.Where(entry => entry.IsDirectory
            && (scope is null || entry.Path == scope || entry.Path.StartsWith(scope + '/', StringComparison.Ordinal)))
            .OrderByDescending(entry => entry.Path.Length))
        {
            guard();
            string path = Resolve(root, entry.Path);
            if (File.Exists(path)) throw new IOException("update.directory_replaced");
            if (Directory.Exists(path)) Directory.Delete(path, false);
        }
    }
}
