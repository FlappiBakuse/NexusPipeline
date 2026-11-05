namespace NexusPipeline.Core.Maintenance;

public sealed class RetainedResourceReferences : IDisposable
{
    private readonly object _gate = new();
    private readonly ResourceLeaseManager _owner;
    private bool _disposed, _released;
    private int _uses;
    public Guid OperationId { get; }
    public IReadOnlyList<ResourceClaim> Claims { get; }

    internal RetainedResourceReferences(ResourceLeaseManager owner, Guid operationId, IReadOnlyList<ResourceClaim> claims)
    { _owner = owner; OperationId = operationId; Claims = claims; }

    public void VerifyHeld(ResourceLeaseManager expectedOwner, Guid expectedOperationId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(_owner, expectedOwner) || OperationId != expectedOperationId)
                throw new InvalidOperationException("resource_retained_reference_unbound");
        }
    }

    public T Use<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _uses++; }
        try { return action(); }
        finally
        {
            bool release;
            lock (_gate) { _uses--; release = Detach(); }
            if (release) _owner.ReleaseReferences(Claims);
        }
    }

    public void Dispose()
    {
        bool release;
        lock (_gate) { _disposed = true; release = Detach(); }
        if (release) _owner.ReleaseReferences(Claims);
    }

    private bool Detach()
    {
        if (!_disposed || _uses != 0 || _released) return false;
        _released = true; return true;
    }
}
