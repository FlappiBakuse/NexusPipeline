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
        return new(root, identity, files);
    }

    internal void Discard()
    {
        using (var lease = StableDirectoryLease.Acquire(_root))
        {
            if (PhysicalObjectIdentity.Read(_root) != _identity) throw new IOException("inspection_copy_replaced");
            var actual = Directory.EnumerateFileSystemEntries(_root).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (actual.Any(path => !Known(path)) || _files.Any(file => !actual.Contains(file.Path)))
                throw new IOException("inspection_copy_unknown_entry");
            foreach (var file in _files)
            {
                using var input = File.OpenRead(file.Path);
                if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != file.Identity || input.Length != file.SizeBytes ||
                    Convert.ToHexStringLower(SHA256.HashData(input)) != file.Sha256) throw new IOException("inspection_copy_changed");
            }
            // SQLite read-only admission creates its WAL/SHM in this exclusively owned database group.
            // Admit only its reserved names; arbitrary extra entries keep the diagnostic copy intact.
            var final = Capture(_root, _identity);
            foreach (var file in final._files) PhysicalObjectDeletion.DeleteVerified(file);
        }
        PhysicalObjectDeletion.DeleteVerified(new(_root, _parent, _identity, FileObjectKind.Directory, 0, null));
    }

    private static bool Known(string path) => Path.GetFileName(path) is "nexus.db" or "nexus.db-wal" or "nexus.db-shm" or "nexus.db-journal";
}
