using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class PreparedPatchBatch : IDisposable
{
    private readonly object _gate = new();
    private readonly ConfigurationRevisionBatch _batch;
    private readonly OperationIdentity _operation;
    internal IReadOnlyList<ConfigurationReadStamp> ReadSet { get; }
    private List<PreparedConfigurationPatch>? _documents;
    internal Guid InstanceId { get; }
    internal string RuntimeRoot { get; }
    public Guid OperationId => _operation.OperationId;
    public int DocumentCount { get; }
    public int ResourceCount => _batch.Changes.Length;
    public bool HasChanges { get; }

    internal PreparedPatchBatch(RuntimeInstance instance, ConfigurationRevisionBatch batch,
        OperationIdentity operation, List<PreparedConfigurationPatch> documents, IReadOnlyList<ConfigurationReadStamp> readSet)
    {
        InstanceId = instance.InstanceId; RuntimeRoot = instance.RuntimeRoot; _batch = batch; _operation = operation;
        _documents = documents; ReadSet = readSet; DocumentCount = documents.Count; HasChanges = documents.Any(document => document.HasChanges);
    }

    internal T Use<T>(Func<ConfigurationRevisionBatch, OperationIdentity, IReadOnlyList<PreparedConfigurationPatch>, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_documents is null, this);
            return action(_batch, _operation, _documents);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_documents is null) return;
            for (var index = _documents.Count - 1; index >= 0; index--) _documents[index].Dispose();
            _documents = null;
        }
    }
    public override string ToString() => nameof(PreparedPatchBatch);
}
