namespace NexusPipeline.Core.Maintenance;

public sealed class HostCompletionDrain : IDisposable
{
    private readonly HostLifecycle _owner;
    public Guid OperationId { get; }

    internal HostCompletionDrain(HostLifecycle owner, Guid operationId)
    { _owner = owner; OperationId = operationId; }

    public bool TryReserve() => _owner.ReserveCompletion(this);
    public bool TryEnterEffect() => _owner.EnterCompletionEffect(this);
    public bool TryCancel() => _owner.CancelCompletion(this, effectRejected: false);
    public bool ReleaseFinishedEffect() => _owner.CancelCompletion(this, effectRejected: true);
    public void Dispose() => TryCancel();
}
