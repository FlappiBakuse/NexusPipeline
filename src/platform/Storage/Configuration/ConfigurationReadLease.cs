using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class ConfigurationReadLease : IDisposable
{
    private readonly List<IDisposable> _held = [];

    internal ConfigurationReadLease(RuntimeInstance instance, ConfigurationRevisionStore revisions,
        ImmutableBlobStore blobs, IReadOnlyList<ConfigurationReadStamp> readSet)
    {
        if (readSet.Count == 0) return;
        try
        {
            _held.Add(StableDirectoryLease.Acquire(Path.Combine(instance.RuntimeRoot, "blobs/content")));
            var resources = new Dictionary<ConfigurationAddress, ConfigurationReadFiles>();
            long total = 0;
            foreach (var item in readSet)
            {
                if (!resources.TryGetValue(item.Address, out var files))
                {
                    var current = revisions.Read(item.Address) ?? throw new ConfigurationException("configuration_read_set_changed");
                    files = new(current);
                    resources.Add(item.Address, files);
                }
                var entry = files.Require(item);
                if ((total += entry.SizeBytes) > 32 * 1024 * 1024) throw new ConfigurationException("configuration_read_set_limit");
                _held.Add(blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!)));
            }
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
}

internal sealed class ConfigurationReadFiles(ConfigurationRevision revision)
{
    private readonly IReadOnlyDictionary<string, FileTreeEntry> _files = revision.Tree.Entries
        .Where(entry => entry.Kind == FileObjectKind.File).ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);

    internal FileTreeEntry Require(ConfigurationReadStamp item)
    {
        if (revision.Address != item.Address || revision.Revision != item.ExpectedRevision || revision.Contract != item.Contract ||
            !_files.TryGetValue(item.RelativePath, out var entry) || entry.Sha256 != item.SourceSha256)
            throw new ConfigurationException("configuration_read_set_changed");
        return entry;
    }
}
