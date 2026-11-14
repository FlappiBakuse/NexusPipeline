using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class QueueActivityOwner(ResourceLeaseManager resources, Action? signal = null) : IDisposable
{
    private readonly object _gate = new();
    private AcceptedNotificationQueue? _queue;
    private long _epoch;
    private int _registrations;
    private bool _closed;

    internal IDisposable BeginRegistration()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _registrations = checked(_registrations + 1); _epoch = checked(_epoch + 1);
            return new Registration(this);
        }
    }

    internal AcceptedNotificationQueue Ensure(ResourceOperationLease root)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _queue ??= resources.RegisterNotificationQueue(root);
        }
    }

    internal void Ensure(AcceptedExecutionRun run)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _queue ??= resources.RegisterNotificationQueue(run, Guid.NewGuid());
        }
    }

    internal ResourceOperationLease? Continue(Guid operation)
    {
        lock (_gate)
            return _closed || _queue is null ? null : resources.TryContinueQueue(_queue, operation).Lease;
    }

    internal long? ReadReleaseEpoch()
    {
        lock (_gate) return !_closed && _queue is not null && _registrations == 0 ? _epoch : null;
    }

    internal bool TryRelease(long epoch)
    {
        lock (_gate)
        {
            if (_closed || _registrations != 0 || _epoch != epoch) return false;
            _queue?.Dispose(); _queue = null; return true;
        }
    }

    internal bool IsActive { get { lock (_gate) return _queue is not null; } }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_registrations != 0) throw new IOException("notification_registration_active");
            _closed = true; _queue?.Dispose(); _queue = null;
        }
    }

    private void EndRegistration()
    {
        lock (_gate) { _registrations--; _epoch = checked(_epoch + 1); }
        signal?.Invoke();
    }

    private sealed class Registration(QueueActivityOwner owner) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.EndRegistration(); }
    }
}
