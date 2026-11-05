using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Core.Maintenance;

public sealed class AcceptedExecutionRun : IDisposable
{
    private readonly HostLifecycle _lifecycle;
    internal ResourceLeaseManager ResourceOwner { get; }
    internal IReadOnlyList<ResourceClaim> Claims { get; private set; }
    internal QueuedExecutionSource? QueuedSource { get; private set; }
    internal bool IsQueued { get; private set; }
    public Guid RunId { get; }
    public bool IsActive => _lifecycle.IsRunActive(this);

    internal AcceptedExecutionRun(HostLifecycle lifecycle, ResourceLeaseManager resourceOwner, Guid runId, IReadOnlyList<ResourceClaim> claims)
    { _lifecycle = lifecycle; ResourceOwner = resourceOwner; RunId = runId; Claims = claims; }

    internal void BindQueue(OperationIdentity operation, ExecutionSelectionRequest request)
    { QueuedSource = new(operation, ExecutionSelectionPolicy.FreezeRequest(request)); IsQueued = true; }

    internal void Promote(IReadOnlyList<ResourceClaim> claims)
    { Claims = claims; IsQueued = false; }

    internal void RequireContinuation(Guid operationId, string subject, ExecutionSelectionRequest request)
        => _lifecycle.RequireAcceptedContinuation(this, operationId, subject, request);

    public void Dispose() => _lifecycle.ReleaseAcceptedRun(this);
}

internal sealed record QueuedExecutionSource(OperationIdentity Operation, ExecutionSelectionRequest Request);
