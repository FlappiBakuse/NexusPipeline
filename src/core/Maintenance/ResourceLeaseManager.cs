using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Maintenance;

public enum ResourceClaimMode { Reference, Mutation }
public enum ResourceLeaseDenial { None, HostUnavailable, Conflict }
public sealed record ResourceClaim(string ResourceKey, ResourceClaimMode Mode);
public sealed record ResourceLeaseAdmission(ResourceOperationLease? Lease, ResourceLeaseDenial Denial);

public sealed class ResourceLeaseManager(HostLifecycle lifecycle)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private TaskCompletionSource? _changed;
    private int _waiters;
    public bool UsesRecoveryIndex(IRecoveryIndex candidate) => lifecycle.UsesRecoveryIndex(candidate);

    public ResourceLeaseAdmission TryAcquire(Guid operationId, IReadOnlyList<ResourceClaim> claims)
        => Acquire(operationId, claims, null);

    public async ValueTask<ResourceLeaseAdmission> AcquireAsync(Guid operationId, IReadOnlyList<ResourceClaim> claims,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frozen = FreezeClaims(claims);
        var admission = TryAcquire(operationId, frozen);
        if (admission.Denial != ResourceLeaseDenial.Conflict) return admission;
        if (Interlocked.Increment(ref _waiters) > 128)
        {
            Interlocked.Decrement(ref _waiters);
            return admission;
        }
        try
        {
            while (true)
            {
                Task changed;
                lock (_gate) changed = (_changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                cancellationToken.ThrowIfCancellationRequested();
                admission = TryAcquire(operationId, frozen);
                if (admission.Denial != ResourceLeaseDenial.Conflict) return admission;
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Interlocked.Decrement(ref _waiters); }
    }

    public ResourceLeaseAdmission TryCompletePayload(HostCompletionDrain drain)
    {
        ArgumentNullException.ThrowIfNull(drain);
        return Acquire(drain.OperationId, PayloadCompletionClaims.All, null, hostPayloadCompletion: drain);
    }

    public PreparedDesktopExit PrepareDesktopExit(HostCompletionDrain drain, ResourceOperationLease root)
    {
        ArgumentNullException.ThrowIfNull(drain); ArgumentNullException.ThrowIfNull(root);
        var held = root.Borrow(this, drain.OperationId, PayloadCompletionClaims.All);
        try { return new(lifecycle, drain, held); }
        catch { held.Dispose(); throw; }
    }

    public ResourceLeaseAdmission TryContinue(RecoveryContinuation continuation, IReadOnlyList<ResourceClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return Acquire(continuation.OperationId, claims, continuation);
    }

    public ResourceLeaseAdmission TryCompleteSnapshots(SnapshotCompletionPermit permit, IReadOnlyList<ResourceClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(permit);
        return Acquire(permit.Completion.ParentOperationId, claims, null, permit);
    }

    public ResourceLeaseAdmission TryContinueRun(AcceptedExecutionRun run, Guid operationId, IReadOnlyList<ResourceClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Acquire(operationId, claims, null, acceptedRun: run);
    }

    public AcceptedNotificationQueue RegisterNotificationQueue(ResourceOperationLease metadataRoot)
    {
        ArgumentNullException.ThrowIfNull(metadataRoot);
        using var held = metadataRoot.Borrow(this, metadataRoot.OperationId,
            [new(NotificationQueueResources.Queue, ResourceClaimMode.Mutation)]);
        return held.Use(() => lifecycle.RegisterNotificationQueue(this, metadataRoot.OperationId, null));
    }

    public AcceptedNotificationQueue RegisterNotificationQueue(AcceptedExecutionRun run, Guid queueOperationId)
    {
        ArgumentNullException.ThrowIfNull(run);
        return lifecycle.RegisterNotificationQueue(this, queueOperationId, run);
    }

    public ResourceLeaseAdmission TryContinueQueue(AcceptedNotificationQueue queue, Guid deliveryOperationId)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return Acquire(deliveryOperationId, NotificationQueueResources.SenderClaims, null, notificationQueue: queue);
    }

    internal AcceptedExecutionRun AcceptRun(ResourceOperationLease root, Guid operationId, Guid runId)
    {
        using var held = root.Borrow(this, operationId, root.Claims);
        return held.Use(() => lifecycle.AcceptRun(this, runId, root.Claims, operationId));
    }

    public AcceptedExecutionRun RegisterQueuedRun(ResourceOperationLease metadataRoot, Guid runId,
        OperationIdentity operation, ExecutionSelectionRequest request)
    {
        using var held = metadataRoot.Borrow(this, metadataRoot.OperationId, [new("scheduling:state", ResourceClaimMode.Mutation)]);
        return held.Use(() => lifecycle.RegisterQueuedRun(this, runId, operation, request, metadataRoot.OperationId));
    }

    public AcceptedExecutionRun? FindQueuedRun(Guid runId, OperationIdentity operation, ExecutionSelectionRequest request)
        => lifecycle.FindQueuedRun(this, runId, operation, request);

    public AcceptedExecutionRun? FindQueuedRun(Guid runId, Guid operationId, string subject, QueueTarget target)
        => lifecycle.FindQueuedRun(this, runId, operationId, subject, target);

    public void RefreshQueuedRun(ResourceOperationLease metadataRoot, AcceptedExecutionRun run,
        OperationIdentity operation, ExecutionSelectionRequest request)
    {
        using var held = metadataRoot.Borrow(this, metadataRoot.OperationId, [new("scheduling:state", ResourceClaimMode.Mutation)]);
        held.Use(() => { lifecycle.RefreshQueuedRun(this, run, operation, request); return true; });
    }

    public void ReportUncertainOwnership(ResourceOperationLease root, string reason)
    {
        using var held = root.Borrow(this, root.OperationId, root.Claims);
        held.Use(() => { lifecycle.ReportUncertainOwnership(reason); return true; });
    }

    private ResourceLeaseAdmission Acquire(Guid operationId, IReadOnlyList<ResourceClaim> claims, RecoveryContinuation? continuation,
        SnapshotCompletionPermit? snapshotCompletion = null, AcceptedExecutionRun? acceptedRun = null,
        AcceptedNotificationQueue? notificationQueue = null, HostCompletionDrain? hostPayloadCompletion = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("resource_operation_identity_required");
        var frozen = FreezeClaims(claims);
        IDisposable? write = hostPayloadCompletion is not null ? lifecycle.EnterPayloadCompletion(hostPayloadCompletion, frozen) :
            notificationQueue is not null ? lifecycle.TryEnterQueue(notificationQueue, this, operationId, frozen) :
            acceptedRun is not null ? lifecycle.TryEnterRun(acceptedRun, this, operationId, frozen) :
            snapshotCompletion is not null ? lifecycle.EnterSnapshotCompletion(snapshotCompletion, frozen) :
            continuation is null ? lifecycle.TryEnterWrite(frozen, operationId) : lifecycle.TryEnterContinuation(continuation, frozen);
        if (write is null) return new(null, ResourceLeaseDenial.HostUnavailable);
        try
        {
            lock (_gate)
            {
                if (frozen.Any(Conflicts)) return new(null, ResourceLeaseDenial.Conflict);
                var leaseId = Guid.NewGuid();
                foreach (var claim in frozen)
                {
                    if (!_entries.TryGetValue(claim.ResourceKey, out var entry)) _entries.Add(claim.ResourceKey, entry = new());
                    if (claim.Mode == ResourceClaimMode.Mutation) entry.Mutator = leaseId;
                    else entry.References = checked(entry.References + 1);
                }
                var lease = new ResourceOperationLease(this, lifecycleWrite: write, leaseId, operationId, frozen);
                write = null;
                return new(lease, ResourceLeaseDenial.None);
            }
        }
        finally { write?.Dispose(); }
    }

    internal static IReadOnlyList<ResourceClaim> FreezeClaims(IReadOnlyList<ResourceClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var frozen = Array.AsReadOnly(claims.ToArray());
        if (frozen.Count is < 1 or > 4096 || frozen.Any(item => item is null || string.IsNullOrWhiteSpace(item.ResourceKey) ||
            item.ResourceKey.Length > 512 || item.ResourceKey != item.ResourceKey.Trim() || item.ResourceKey.Any(char.IsControl) || !Enum.IsDefined(item.Mode)) ||
            frozen.Select(item => item.ResourceKey).Distinct(StringComparer.Ordinal).Count() != frozen.Count)
            throw new ArgumentException("resource_claims_invalid");
        return frozen;
    }

    private bool Conflicts(ResourceClaim claim) => _entries.TryGetValue(claim.ResourceKey, out var entry) &&
        (entry.Mutator.HasValue || claim.Mode == ResourceClaimMode.Mutation && entry.References != 0);

    internal RetainedResourceReferences RetainReferences(Guid operationId, IReadOnlyList<ResourceClaim> claims)
    {
        lock (_gate)
        {
            if (claims.Any(claim => claim.Mode != ResourceClaimMode.Reference || !_entries.TryGetValue(claim.ResourceKey, out var entry) ||
                entry.Mutator.HasValue || entry.References <= 0 || entry.References == int.MaxValue))
                throw new InvalidOperationException("resource_retained_reference_unbound");
            foreach (var claim in claims) _entries[claim.ResourceKey].References++;
            return new(this, operationId, claims);
        }
    }

    internal void Release(ResourceOperationLease lease) => ReleaseClaims(lease.LeaseId, lease.Claims);
    internal void ReleaseReferences(IReadOnlyList<ResourceClaim> claims) => ReleaseClaims(Guid.Empty, claims);

    private void ReleaseClaims(Guid leaseId, IReadOnlyList<ResourceClaim> claims)
    {
        TaskCompletionSource? changed;
        lock (_gate)
        {
            foreach (var claim in claims)
            {
                if (!_entries.TryGetValue(claim.ResourceKey, out var entry)) throw new InvalidOperationException("resource_lease_accounting_invalid");
                if (claim.Mode == ResourceClaimMode.Mutation)
                {
                    if (entry.Mutator != leaseId) throw new InvalidOperationException("resource_mutation_lease_unbound");
                    entry.Mutator = null;
                }
                else
                {
                    if (entry.References <= 0) throw new InvalidOperationException("resource_reference_lease_unbound");
                    entry.References--;
                }
                if (!entry.Mutator.HasValue && entry.References == 0) _entries.Remove(claim.ResourceKey);
            }
            changed = _changed;
            _changed = null;
        }
        changed?.TrySetResult();
    }

    private sealed class Entry
    {
        internal Guid? Mutator;
        internal int References;
    }
}

public sealed class ResourceOperationLease : IDisposable
{
    private readonly object _gate = new();
    private ResourceLeaseManager? _owner;
    private readonly IDisposable _lifecycleWrite;
    private int _disposed;
    private int _borrows;
    private TaskCompletionSource? _releaseSignal;
    private Exception? _releaseFailure;
    private bool _releaseComplete;
    public Guid LeaseId { get; }
    public Guid OperationId { get; }
    public IReadOnlyList<ResourceClaim> Claims { get; }
    public bool IsActive => Volatile.Read(ref _disposed) == 0;
    public Task WhenReleased
    {
        get
        {
            lock (_gate)
            {
                if (_releaseComplete) return _releaseFailure is null ? Task.CompletedTask : Task.FromException(_releaseFailure);
                return (_releaseSignal ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
    }

    internal ResourceOperationLease(ResourceLeaseManager owner, IDisposable lifecycleWrite, Guid leaseId,
        Guid operationId, IReadOnlyList<ResourceClaim> claims)
    {
        _owner = owner;
        _lifecycleWrite = lifecycleWrite;
        LeaseId = leaseId;
        OperationId = operationId;
        Claims = claims;
    }

    public ResourceOperationBorrow Borrow(ResourceLeaseManager expectedOwner, Guid expectedOperationId, IReadOnlyList<ResourceClaim> requiredClaims)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        if (expectedOperationId == Guid.Empty) throw new ArgumentException("resource_operation_identity_required");
        var required = ResourceLeaseManager.FreezeClaims(requiredClaims);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!ReferenceEquals(_owner, expectedOwner) || OperationId != expectedOperationId ||
                required.Any(request => !Claims.Any(held => held.ResourceKey == request.ResourceKey &&
                    (request.Mode == ResourceClaimMode.Reference || held.Mode == ResourceClaimMode.Mutation))))
                throw new InvalidOperationException("resource_operation_borrow_unbound");
            var borrow = new ResourceOperationBorrow(this);
            _borrows = checked(_borrows + 1);
            return borrow;
        }
    }

    public RetainedResourceReferences RetainReferences(ResourceLeaseManager expectedOwner, Guid expectedOperationId,
        IReadOnlyList<ResourceClaim> requiredClaims)
    {
        var required = ResourceLeaseManager.FreezeClaims(requiredClaims);
        if (required.Any(claim => claim.Mode != ResourceClaimMode.Reference || !Claims.Any(held =>
                held.ResourceKey == claim.ResourceKey && held.Mode == ResourceClaimMode.Reference)))
            throw new InvalidOperationException("resource_retained_reference_unbound");
        using var borrow = Borrow(expectedOwner, expectedOperationId, required);
        return borrow.Use(() => expectedOwner.RetainReferences(expectedOperationId, required));
    }

    public AcceptedExecutionRun AcceptRun(ResourceLeaseManager expectedOwner, Guid expectedOperationId, Guid runId)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        return expectedOwner.AcceptRun(this, expectedOperationId, runId);
    }

    public void Dispose()
    {
        ResourceLeaseManager? owner;
        lock (_gate)
        {
            if (_disposed != 0) return;
            Volatile.Write(ref _disposed, 1);
            owner = DetachIfReleased();
        }
        Release(owner);
    }

    internal void ReturnBorrow()
    {
        ResourceLeaseManager? owner;
        lock (_gate)
        {
            if (_borrows <= 0) throw new InvalidOperationException("resource_borrow_accounting_invalid");
            _borrows--;
            owner = DetachIfReleased();
        }
        Release(owner);
    }

    private ResourceLeaseManager? DetachIfReleased()
    {
        if (_disposed == 0 || _borrows != 0) return null;
        var owner = _owner; _owner = null; return owner;
    }

    private void Release(ResourceLeaseManager? owner)
    {
        if (owner is null) return;
        try
        {
            try { owner.Release(this); }
            finally { _lifecycleWrite.Dispose(); }
        }
        catch (Exception error) { CompleteRelease(error); throw; }
        CompleteRelease(null);
    }

    private void CompleteRelease(Exception? failure)
    {
        TaskCompletionSource? signal;
        lock (_gate)
        {
            _releaseFailure = failure;
            _releaseComplete = true;
            signal = _releaseSignal;
        }
        if (failure is null) signal?.TrySetResult();
        else signal?.TrySetException(failure);
    }
}

public sealed class ResourceOperationBorrow : IDisposable
{
    private readonly object _gate = new();
    private ResourceOperationLease? _lease;
    private bool _disposed;
    private int _activeUses;
    internal ResourceOperationBorrow(ResourceOperationLease lease) => _lease = lease;

    public void VerifyHeld()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public T Use<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeUses = checked(_activeUses + 1);
        }
        try { return action(); }
        finally
        {
            ResourceOperationLease? lease;
            lock (_gate) { _activeUses--; lease = DetachIfReleased(); }
            lease?.ReturnBorrow();
        }
    }

    public void Dispose()
    {
        ResourceOperationLease? lease;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; lease = DetachIfReleased();
        }
        lease?.ReturnBorrow();
    }

    private ResourceOperationLease? DetachIfReleased()
    {
        if (!_disposed || _activeUses != 0) return null;
        var lease = _lease; _lease = null; return lease;
    }
}
