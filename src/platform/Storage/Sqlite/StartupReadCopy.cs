using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class StartupReadCopy : IDisposable
{
    private readonly string _root;
    private readonly PhysicalObjectIdentity _identity;
    internal string Path { get; }

    internal StartupReadCopy(string source, string runtimeRoot)
    {
        var parent = PhysicalPathPolicy.NormalizeWithoutLinks(System.IO.Path.Combine(runtimeRoot, "diagnostics", "startup-presentation"));
        Directory.CreateDirectory(parent);
        _root = System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N"));
        ExclusiveDirectory.Create(_root);
        _identity = PhysicalObjectIdentity.Read(_root);
        Path = System.IO.Path.Combine(_root, "nexus.db");
        var inputs = new List<(string Suffix, FileStream Stream)>();
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-journal" })
            {
                var file = PhysicalPathPolicy.NormalizeWithoutLinks(source + suffix);
                if (suffix.Length > 0 && !File.Exists(file)) continue;
                var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                inputs.Add((suffix, input));
                if (suffix == "-journal" && input.Length > 0) throw new IOException("database_rollback_journal_requires_readonly_diagnosis");
            }
            // SQLite may create or change shared memory even for a read-only connection.
            // Only this isolated file group is opened before full admission.
            foreach (var (suffix, input) in inputs)
            {
                using var output = new FileStream(Path + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }
        catch { Dispose(); throw; }
        finally { foreach (var (_, input) in inputs) input.Dispose(); }
    }

    public void Dispose()
    {
        using var held = StableDirectoryLease.Acquire(_root);
        if (PhysicalObjectIdentity.Read(_root) != _identity) throw new IOException("startup_presentation_copy_changed");
        var entries = Directory.EnumerateFileSystemEntries(_root).ToArray();
        if (entries.Any(entry => !new[] { "nexus.db", "nexus.db-wal", "nexus.db-shm", "nexus.db-journal" }
                .Contains(System.IO.Path.GetFileName(entry)) || !File.Exists(entry)))
            throw new IOException("startup_presentation_copy_unknown_entry");
        foreach (var entry in entries)
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(entry);
            File.Delete(entry);
        }
        held.Dispose();
        Directory.Delete(_root);
    }
}
