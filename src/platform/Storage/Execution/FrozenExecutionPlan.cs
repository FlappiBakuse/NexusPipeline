using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class FrozenExecutionPlan
{
    private readonly byte[] _bytes;
    internal FrozenExecutionPlan(byte[] bytes) => _bytes = bytes.ToArray();
    public ExecutionPlan ReadWire() => JsonSerializer.Deserialize<ExecutionPlan>(_bytes)!;
    public byte[] Export() => _bytes.ToArray();
}

public sealed class PreparedExecutionPlan : IDisposable
{
    private List<IDisposable>? _held;
    private readonly FrozenExecutionPlan _plan;
    private readonly BlobReference _reference;
    private readonly PlanInputLease _inputs;

    internal PreparedExecutionPlan(FrozenExecutionPlan plan, BlobReference reference, List<IDisposable> held, PlanInputLease inputs)
        { _plan = plan; _reference = reference; _held = held; _inputs = inputs; }

    internal void RequireInputs(BlobReference reference, ExecutionPlan wire)
    {
        if (Reference != reference) throw new IOException("run_attempt_total_plan_unbound");
        _inputs.Require(wire);
    }

    public FrozenExecutionPlan Plan
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
