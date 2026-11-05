namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed class SecretOperationFlights
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Slot> _slots = [];
    private int _waitingAndActive;

    internal async ValueTask<Lease> EnterAsync(Guid operationId, CancellationToken cancellationToken)
    {
        Slot slot;
        lock (_gate)
        {
            if (_waitingAndActive >= 64) throw new InvalidOperationException("secret_operation_queue_full");
            if (!_slots.TryGetValue(operationId, out slot!)) _slots.Add(operationId, slot = new());
            slot.References++; _waitingAndActive++;
        }
        try { await slot.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { ReleaseReference(operationId, slot); throw; }
        return new Lease(this, operationId, slot);
    }

    private void ReleaseReference(Guid id, Slot slot)
    {
        lock (_gate)
        {
            slot.References--; _waitingAndActive--;
            if (slot.References == 0) { _slots.Remove(id); slot.Semaphore.Dispose(); }
        }
    }

    internal sealed class Slot { internal readonly SemaphoreSlim Semaphore = new(1, 1); internal int References; }
    internal sealed class Lease(SecretOperationFlights owner, Guid id, Slot slot) : IDisposable
    {
        private int _disposed;
        internal void Require(SecretOperationFlights expected, Guid operationId)
        {
            if (!ReferenceEquals(owner, expected) || id != operationId || Volatile.Read(ref _disposed) != 0)
                throw new InvalidOperationException("secret_operation_reservation_unbound");
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            slot.Semaphore.Release(); owner.ReleaseReference(id, slot);
        }
    }
}
