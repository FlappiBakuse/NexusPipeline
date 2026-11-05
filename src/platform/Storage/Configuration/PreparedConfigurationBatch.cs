using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class PreparedConfigurationBatch : IDisposable
{
    private readonly object _gate = new();
    private List<PreparedConfigurationRevision>? _items;
    internal ConfigurationRevisionBatch Batch { get; }
    internal Guid InstanceId { get; }
    internal string RuntimeRoot { get; }

    internal PreparedConfigurationBatch(RuntimeInstance instance, ConfigurationRevisionBatch batch, List<PreparedConfigurationRevision> items)
    { InstanceId = instance.InstanceId; RuntimeRoot = instance.RuntimeRoot; Batch = batch; _items = items; }

    internal T Use<T>(Func<ConfigurationRevisionBatch, IReadOnlyList<BlobReference>, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_items is null, this);
            var manifests = _items.Select(item => item.Use((_, manifest) => manifest)).ToArray();
            return action(Batch, manifests);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_items is null) return;
            for (var index = _items.Count - 1; index >= 0; index--) _items[index].Dispose();
            _items = null;
        }
    }
}
