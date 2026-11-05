using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AttemptBoundInputs(RunAttemptPlan plan, ExecutionPlan total, List<IDisposable> held) : IDisposable
{
    private List<IDisposable>? _held = held;
    internal RunAttemptPlan Plan { get { RequireOpen(); return plan; } }
    internal ExecutionPlan Total { get { RequireOpen(); return total; } }

    private void RequireOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this);

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref _held, null);
        if (owned is null) return;
        for (var index = owned.Count - 1; index >= 0; index--) owned[index].Dispose();
    }
}
