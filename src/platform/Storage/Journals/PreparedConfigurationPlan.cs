namespace NexusPipeline.Platform.Storage.Journals;

public sealed class PreparedConfigurationPlan : IDisposable
{
    private List<FileStream>? _held;
    private readonly FrozenConfigurationExchange _plan;
    private readonly BlobReference _reference;

    internal PreparedConfigurationPlan(FrozenConfigurationExchange plan, BlobReference reference, List<FileStream> held)
    {
        _plan = plan;
        _reference = reference;
        _held = held;
    }

    public FrozenConfigurationExchange Plan
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
