namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class DatabaseWriteGate
{
    private readonly SemaphoreSlim _pending;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly TimeSpan _waitTimeout;
    public int Capacity { get; }
    public int PendingCount => Capacity - _pending.CurrentCount;

    public DatabaseWriteGate(int capacity, TimeSpan waitTimeout)
    {
        if (capacity is < 1 or > 1024 || waitTimeout <= TimeSpan.Zero || waitTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _pending = new(capacity, capacity);
        _waitTimeout = waitTimeout;
    }

    public IDisposable Enter(CancellationToken cancellationToken = default)
    {
        if (!_pending.Wait(0, cancellationToken)) throw new IOException("database_writer_capacity_exceeded");
        try
        {
            if (!_writer.Wait(_waitTimeout, cancellationToken)) throw new IOException("database_writer_busy");
            return new Lease(this);
        }
        catch
        {
            _pending.Release();
            throw;
        }
    }

    private sealed class Lease(DatabaseWriteGate owner) : IDisposable
    {
        private DatabaseWriteGate? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            current._writer.Release();
            current._pending.Release();
        }
    }
}
