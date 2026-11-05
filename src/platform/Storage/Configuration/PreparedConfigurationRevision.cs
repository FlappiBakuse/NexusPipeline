using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class PreparedConfigurationRevision : IDisposable
{
    private readonly object _gate = new();
    private readonly ConfigurationRevisionChange _change;
    private readonly BlobReference _manifest;
    private List<IDisposable>? _held;
    internal Guid InstanceId { get; }
    internal string RuntimeRoot { get; }

    internal PreparedConfigurationRevision(RuntimeInstance instance, ConfigurationRevisionChange change, BlobReference manifest, List<IDisposable> held)
    { InstanceId = instance.InstanceId; RuntimeRoot = instance.RuntimeRoot; _change = change; _manifest = manifest; _held = held; }

    internal T Use<T>(Func<ConfigurationRevisionChange, BlobReference, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_held is null, this);
            return action(_change, _manifest);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_held is null) return;
            for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
            _held = null;
        }
    }
}
