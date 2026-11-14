using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class DatabaseCopy
{
    private readonly string _root;
    private readonly PhysicalObjectIdentity _identity;
    private readonly FileObjectDeletion[] _files;
    private readonly PhysicalObjectIdentity _parent;

    private DatabaseCopy(string root, PhysicalObjectIdentity identity, FileObjectDeletion[] files)
    {
        _root = root; _identity = identity; _files = files;
        _parent = PhysicalObjectIdentity.Read(Path.GetDirectoryName(root)!);
    }

    internal static DatabaseCopy Capture(string root, PhysicalObjectIdentity identity)
    {
        using var lease = StableDirectoryLease.Acquire(root);
        if (PhysicalObjectIdentity.Read(root) != identity) throw new IOException("inspection_copy_replaced");
        var files = Directory.EnumerateFileSystemEntries(root).Select(path =>
        {
            if (!Known(path) || !File.Exists(path)) throw new IOException("inspection_copy_unknown_entry");
            PhysicalObjectIdentity.RequireSingleLink(path);
            using var input = File.OpenRead(path);
            return new FileObjectDeletion(path, identity, PhysicalObjectIdentity.Read(input.SafeFileHandle),
                FileObjectKind.File, input.Length, Convert.ToHexStringLower(SHA256.HashData(input)));
        }).ToArray();
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "startup.database-copy-capture",
            digestReadBytes = files.Sum(file => file.SizeBytes) }));
        return new(root, identity, files);
    }

    internal void Discard()
    {
        using var timing = StartupProfile.Measure("database.discard");
        using (var lease = StableDirectoryLease.Acquire(_root))
        {
            if (PhysicalObjectIdentity.Read(_root) != _identity) throw new IOException("inspection_copy_replaced");
            var actual = Directory.EnumerateFileSystemEntries(_root).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (actual.Any(path => !Known(path)) || _files.Any(file => !actual.Contains(file.Path)))
                throw new IOException("inspection_copy_unknown_entry");
            // Capture verifies the whole group once; compare the original members before deleting any file.
            // Native recovery may remove a hot super-journal or leave an inactive rollback file.
            var final = Capture(_root, _identity);
            var current = final._files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var file in _files)
                if (!current.TryGetValue(file.Path, out var observed) || observed != file)
                    throw new IOException("inspection_copy_changed");
            foreach (var file in final._files) PhysicalObjectDeletion.DeleteInspectionCopy(file);
            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "startup.database-copy-delete",
                digestReadBytes = final._files.Sum(file => file.SizeBytes) }));
        }
        PhysicalObjectDeletion.DeleteVerified(new(_root, _parent, _identity, FileObjectKind.Directory, 0, null));
    }

    private static bool Known(string path) => DatabaseJournals.Known(Path.GetFileName(path));
}
