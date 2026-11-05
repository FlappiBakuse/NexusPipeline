namespace NexusPipeline.Core.Maintenance;

public sealed class PreparedDesktopExit : IDisposable
{
    private readonly object _gate = new();
    private readonly HostLifecycle _owner;
    private readonly HostCompletionDrain _drain;
    private ResourceOperationBorrow? _held;
    private bool _used;

    internal PreparedDesktopExit(HostLifecycle owner, HostCompletionDrain drain, ResourceOperationBorrow held)
    { _owner = owner; _drain = drain; _held = held; }

    public HostCloseDecision RequestPermit(HostLifecycle actualOwner, Guid sessionId, Guid correlationId)
    {
        lock (_gate)
        {
            if (_used || !ReferenceEquals(_owner, actualOwner) || _held is not { } held)
                throw new InvalidOperationException("maintenance_desktop_exit_owner_unbound");
            var decision = held.Use(() => _owner.RequestDesktopExit(_drain, sessionId, correlationId));
            _used = decision.Permit is not null; return decision;
        }
    }

    public void Dispose()
    {
        ResourceOperationBorrow? held;
        lock (_gate) { held = _held; _held = null; }
        held?.Dispose();
    }
}
