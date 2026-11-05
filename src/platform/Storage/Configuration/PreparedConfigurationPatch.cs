using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class PreparedConfigurationPatch : IDisposable
{
    private readonly object _gate = new();
    private readonly ConfigurationRevisionChange _change;
    private readonly OperationIdentity _operation;
    private byte[]? _candidate;
    private IDisposable? _sourceLease;
    internal Guid InstanceId { get; }
    internal string RuntimeRoot { get; }
    public Guid OperationId => _operation.OperationId;
    public int SizeBytes { get; }
    public bool HasChanges { get; }

    internal PreparedConfigurationPatch(RuntimeInstance instance, ConfigurationRevisionChange change, OperationIdentity operation,
        byte[] candidate, IDisposable sourceLease, bool changed)
    {
        InstanceId = instance.InstanceId; RuntimeRoot = instance.RuntimeRoot; _change = change; _operation = operation;
        _candidate = candidate; _sourceLease = sourceLease; SizeBytes = candidate.Length; HasChanges = changed;
    }

    internal T Use<T>(Func<ConfigurationRevisionChange, OperationIdentity, ReadOnlyMemory<byte>, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_candidate is null, this);
            return action(_change, _operation, _candidate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_candidate is null) return;
            CryptographicOperations.ZeroMemory(_candidate); _candidate = null;
            _sourceLease?.Dispose(); _sourceLease = null;
        }
    }
    public override string ToString() => nameof(PreparedConfigurationPatch);
}
