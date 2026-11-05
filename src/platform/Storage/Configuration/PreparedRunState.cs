namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class PreparedRunState : IDisposable
{
    private List<FileStream>? _held;
    private readonly FrozenRunState _state;
    private readonly BlobReference _reference;
    internal PreparedRunState(FrozenRunState state, BlobReference reference, List<FileStream> held)
    { _state = state; _reference = reference; _held = held; }

    public FrozenRunState State
    { get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this); return _state; } }
    public BlobReference Reference
    { get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this); return _reference; } }

    public void Dispose()
    {
        var held = Interlocked.Exchange(ref _held, null);
        if (held is null) return;
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
}
