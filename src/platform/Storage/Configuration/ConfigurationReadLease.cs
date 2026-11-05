using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class ConfigurationReadLease : IDisposable
{
    private readonly List<IDisposable> _held = [];

    internal ConfigurationReadLease(RuntimeInstance instance, ConfigurationRevisionStore revisions,
        ImmutableBlobStore blobs, IReadOnlyList<ConfigurationReadStamp> readSet)
    {
        try
        {
            if (readSet.Count > 0) _held.Add(StableDirectoryLease.Acquire(Path.Combine(instance.RuntimeRoot, "blobs/content")));
            long total = 0;
            foreach (var item in readSet)
            {
                var current = revisions.Read(item.Address);
                var entry = current?.Tree.Entries.SingleOrDefault(entry => entry.RelativePath == item.RelativePath && entry.Kind == FileObjectKind.File);
                if (current?.Revision != item.ExpectedRevision || current.Contract != item.Contract || entry?.Sha256 != item.SourceSha256)
                    throw new ConfigurationException("configuration_read_set_changed");
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
