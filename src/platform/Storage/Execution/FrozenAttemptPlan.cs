using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class FrozenAttemptPlan
{
    private readonly byte[] _bytes;
    internal FrozenAttemptPlan(byte[] bytes) => _bytes = bytes.ToArray();
    public RunAttemptPlan ReadWire() => JsonSerializer.Deserialize<RunAttemptPlan>(_bytes)!;
    public byte[] Export() => _bytes.ToArray();
}

public sealed class PreparedAttemptPlan : IDisposable
{
    private List<IDisposable>? _held;
    private readonly FrozenAttemptPlan _plan;
    private readonly BlobReference _reference;

    internal PreparedAttemptPlan(FrozenAttemptPlan plan, BlobReference reference, List<IDisposable> held)
        { _plan = plan; _reference = reference; _held = held; }

    public FrozenAttemptPlan Plan
    {
        get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this); return _plan; }
    }

    public BlobReference Reference
    {
        get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this); return _reference; }
    }

    public void Dispose()
    {
        var held = Interlocked.Exchange(ref _held, null);
        if (held is null) return;
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
}
