using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Maintenance;

public enum HostLifecyclePhase { Running, Draining, ClosePermitted }
public sealed record HostClosePermit(Guid PermitId, Guid SessionId, Guid CorrelationId, long RecoveryRevision, DateTimeOffset ExpiresAt);
public sealed record HostCloseDecision(HostClosePermit? Permit, string ReasonCode);
public sealed record HostCompletionActivity(HostLifecyclePhase Phase, int Operations, int ReferencingOperations,
    int Runs, int NotificationQueues, bool RecoveryReady, int Isolations, int Holds);

public sealed class HostLifecycle(IRecoveryIndex recovery, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AcceptedWrite> _activeWork = [];
    private readonly Dictionary<Guid, AcceptedExecutionRun> _acceptedRuns = [];
    private readonly Dictionary<Guid, AcceptedNotificationQueue> _notificationQueues = [];
    private Dictionary<Guid, Dictionary<string, ResourceClaimMode>>? _drainingOperations;
    private HostLifecyclePhase _phase;
    private HostClosePermit? _permit;
    private Guid? _closingSession;
    private HostCompletionDrain? _completionDrain;
    private bool _completionEffectEntered;
    private bool _completionHostPayloadTaken;
    private Guid? _completionHostPayloadWrite;
    private Dictionary<(RecoveryHoldOwner Owner, Guid OperationId), Dictionary<string, ResourceClaimMode>>? _drainingContinuations;

    public HostLifecyclePhase Phase { get { lock (_gate) return _phase; } }
    public bool UsesRecoveryIndex(IRecoveryIndex candidate) => ReferenceEquals(recovery, candidate);

    public HostCompletionActivity ReadCompletionActivity()
    {
        lock (_gate)
        {
            var verified = recovery.Current;
            return new(_phase, _activeWork.Count,
                _activeWork.Values.Count(work => work.Claims.All(claim => claim.Mode == ResourceClaimMode.Reference)),
                _acceptedRuns.Count, _notificationQueues.Count, verified.IsReady, verified.Isolations.Count, verified.ResourceHolds.Count);
        }
    }

    public IDisposable? TryEnterWrite(IReadOnlySet<string> resourceKeys)
        => TryEnterWrite(resourceKeys.Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).ToArray());

    internal IDisposable? TryEnterWrite(IReadOnlyList<ResourceClaim> claims, Guid? operationId = null)
    {
        lock (_gate)
        {
            var verified = recovery.Current;
            if (_phase != HostLifecyclePhase.Running || !verified.IsReady ||
                verified.Isolations.Any(item => Conflicts(claims, item.ResourceKey, item.Mode)) ||
                verified.ResourceHolds.Any(item => Conflicts(claims, item.ResourceKey, item.Mode))) return null;
            return TrackWrite(operationId ?? Guid.NewGuid(), claims);
        }
    }

    public HostCloseDecision RequestClose(Guid sessionId, Guid correlationId)
    {
        if (sessionId == Guid.Empty || correlationId == Guid.Empty) throw new ArgumentException("Close request identities are required");
        lock (_gate)
        {
            if (_completionDrain is not null) return new(null, "completion_action_pending");
            if (_closingSession is { } previous && previous != sessionId) return new(null, "close_session_mismatch");
            var verified = recovery.Current;
            FreezeDrain(verified);
            _closingSession = sessionId;
            _phase = HostLifecyclePhase.Draining;
            _permit = null;
            if (!verified.IsReady) return new(null, "recovery_unverified");
            if (verified.Isolations.Count != 0 || verified.ResourceHolds.Count != 0) return new(null, "recovery_pending");
            if ((_activeWork.Count != 0 || _acceptedRuns.Count != 0 || _notificationQueues.Count != 0)) return new(null, "accepted_writes_pending");
            _permit = new(Guid.NewGuid(), sessionId, correlationId, verified.Revision, clock.GetUtcNow().AddSeconds(30));
            _phase = HostLifecyclePhase.ClosePermitted;
            return new(_permit, "close_permitted");
        }
    }

    internal IDisposable? TryEnterContinuation(RecoveryContinuation continuation, IReadOnlyList<ResourceClaim> claims)
    {
        var resourceKeys = claims.Select(item => item.ResourceKey).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var verified = recovery.Current;
            if (_phase == HostLifecyclePhase.ClosePermitted ||
                _phase == HostLifecyclePhase.Draining && !AllowsDraining(continuation.Owner, continuation.OperationId, claims) ||
                !continuation.IsAuthorized(recovery, verified, resourceKeys) ||
                claims.Any(claim => claim.Mode == ResourceClaimMode.Mutation && verified.ResourceHolds.Any(hold =>
                    hold.ResourceKey == claim.ResourceKey && hold.Mode != ResourceClaimMode.Mutation))) return null;
            return TrackWrite(continuation.OperationId, claims);
        }
    }

    internal IDisposable? EnterSnapshotCompletion(SnapshotCompletionPermit permit, IReadOnlyList<ResourceClaim> claims)
    {
        lock (_gate)
        {
            var verified = recovery.Current;
            if (_phase == HostLifecyclePhase.ClosePermitted ||
                _phase == HostLifecyclePhase.Draining && !AllowsDraining(RecoveryHoldOwner.ConfigurationOperations, permit.Completion.ParentOperationId, claims) ||
                !permit.IsAuthorized(recovery, verified, claims)) return null;
            return TrackWrite(permit.Completion.ParentOperationId, claims);
        }
    }

    public HostCompletionDrain? BeginCompletionDrain(Guid operationId, bool onlyWhenIdle = false, bool drainReferences = false)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("completion_operation_required");
        if (drainReferences && !onlyWhenIdle) throw new ArgumentException("reference_drain_requires_idle_policy");
        lock (_gate)
        {
            if (_phase != HostLifecyclePhase.Running || _completionDrain is not null ||
                onlyWhenIdle && !(drainReferences ? CompletionIdle() : CompletionReady())) return null;
            var verified = recovery.Current;
            FreezeDrain(verified);
            _phase = HostLifecyclePhase.Draining;
            return _completionDrain = new(this, operationId);
        }
    }

    internal bool ReserveCompletion(HostCompletionDrain drain)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_completionDrain, drain) || _completionEffectEntered ||
                _phase is not (HostLifecyclePhase.Draining or HostLifecyclePhase.ClosePermitted) || !CompletionReady()) return false;
            _phase = HostLifecyclePhase.ClosePermitted;
            return true;
        }
    }

    internal bool EnterCompletionEffect(HostCompletionDrain drain)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_completionDrain, drain) || _completionEffectEntered ||
                _phase != HostLifecyclePhase.ClosePermitted || !CompletionReady()) return false;
            _completionEffectEntered = true;
            return true;
        }
    }

    internal IDisposable? EnterPayloadCompletion(HostCompletionDrain drain, IReadOnlyList<ResourceClaim> claims)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_completionDrain, drain) || !_completionEffectEntered || _completionHostPayloadTaken ||
                _phase != HostLifecyclePhase.ClosePermitted || !claims.SequenceEqual(PayloadCompletionClaims.All) || !CompletionReady()) return null;
            _completionHostPayloadTaken = true;
            var id = Guid.NewGuid();
            _activeWork.Add(id, new(drain.OperationId, Array.AsReadOnly(claims.ToArray())));
            _completionHostPayloadWrite = id;
            return new WriteLease(this, id);
        }
    }

    internal HostCloseDecision RequestDesktopExit(HostCompletionDrain drain, Guid sessionId, Guid correlationId)
    {
        if (sessionId == Guid.Empty || correlationId == Guid.Empty) throw new ArgumentException("Close request identities are required");
        lock (_gate)
        {
            if (!ReferenceEquals(_completionDrain, drain) || !_completionEffectEntered || !_completionHostPayloadTaken ||
                !_completionHostPayloadWrite.HasValue || _phase != HostLifecyclePhase.ClosePermitted)
                return new(null, "maintenance_completion_unbound");
            return new(new(Guid.NewGuid(), sessionId, correlationId, recovery.Current.Revision,
                clock.GetUtcNow().AddSeconds(30)), "maintenance_desktop_close_permitted");
        }
    }

    internal bool CancelCompletion(HostCompletionDrain drain, bool effectRejected)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_completionDrain, drain) || _completionEffectEntered && !effectRejected ||
                _completionHostPayloadWrite.HasValue) return false;
            _completionDrain = null;
            _completionEffectEntered = false;
            _completionHostPayloadTaken = false;
            _drainingContinuations = null;
            _drainingOperations = null;
            _phase = HostLifecyclePhase.Running;
            return true;
        }
    }

    private bool CompletionReady()
    {
        var verified = recovery.Current;
        return _activeWork.Count == 0 && _acceptedRuns.Count == 0 && _notificationQueues.Count == 0 && verified.IsReady && verified.Isolations.Count == 0 && verified.ResourceHolds.Count == 0;
    }

    private bool CompletionIdle()
    {
        var verified = recovery.Current;
        return _activeWork.Values.All(work => work.Claims.All(claim => claim.Mode == ResourceClaimMode.Reference)) &&
            _acceptedRuns.Count == 0 && _notificationQueues.Count == 0 && verified.IsReady &&
            verified.Isolations.Count == 0 && verified.ResourceHolds.Count == 0;
    }

    internal AcceptedExecutionRun AcceptRun(ResourceLeaseManager owner, Guid runId, IReadOnlyList<ResourceClaim> claims, Guid operationId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("execution_run_identity_required");
        lock (_gate)
        {
            if (_phase == HostLifecyclePhase.ClosePermitted) throw new InvalidOperationException("execution_run_activity_unbound");
            if (_acceptedRuns.TryGetValue(runId, out var existing))
            {
                if (!ReferenceEquals(existing.ResourceOwner, owner) || !existing.IsQueued || existing.QueuedSource is not { } queued ||
                    queued.Operation.OperationId != operationId) throw new InvalidOperationException("execution_run_activity_unbound");
                existing.Promote(Array.AsReadOnly(claims.ToArray())); return existing;
            }
            var run = new AcceptedExecutionRun(this, owner, runId, Array.AsReadOnly(claims.ToArray()));
            _acceptedRuns.Add(runId, run); return run;
        }
    }

    internal IDisposable? TryEnterRun(AcceptedExecutionRun run, ResourceLeaseManager owner,
        Guid operationId, IReadOnlyList<ResourceClaim> claims)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(run.ResourceOwner, owner) || !IsRunActive(run) || _phase == HostLifecyclePhase.ClosePermitted ||
                run.IsQueued && run.QueuedSource!.Operation.OperationId != operationId ||
                !run.IsQueued && claims.Any(request => !run.Claims.Any(held => held.ResourceKey == request.ResourceKey &&
                    (request.Mode == ResourceClaimMode.Reference || held.Mode == ResourceClaimMode.Mutation)))) return null;
            var verified = recovery.Current;
            if (!verified.IsReady || verified.Isolations.Any(item => Conflicts(claims, item.ResourceKey, item.Mode)) ||
                verified.ResourceHolds.Any(item => Conflicts(claims, item.ResourceKey, item.Mode))) return null;
            if (_phase == HostLifecyclePhase.Draining)
                _drainingOperations![operationId] = claims.ToDictionary(item => item.ResourceKey, item => item.Mode, StringComparer.Ordinal);
            return TrackWrite(operationId, claims);
        }
    }

    internal AcceptedExecutionRun RegisterQueuedRun(ResourceLeaseManager owner, Guid runId, OperationIdentity operation, ExecutionSelectionRequest request, Guid metadataOperationId)
    {
        if (runId == Guid.Empty || operation.OperationId == Guid.Empty || operation.Kind != "execution.start" ||
            request.Target is not QueueTarget) throw new ArgumentException("execution_queued_source_invalid");
        request = ExecutionSelectionPolicy.FreezeRequest(request);
        lock (_gate)
        {
            if (_acceptedRuns.TryGetValue(runId, out var existing))
            {
                if (!ReferenceEquals(existing.ResourceOwner, owner) || !existing.IsQueued || existing.QueuedSource != new QueuedExecutionSource(operation, request))
                    throw new IOException("execution_queued_source_mismatch");
                return existing;
            }
            var acceptedMetadata = _phase == HostLifecyclePhase.Draining && _drainingOperations!.TryGetValue(metadataOperationId, out var accepted) &&
                accepted.TryGetValue("scheduling:state", out var mode) && mode == ResourceClaimMode.Mutation;
            if (_phase != HostLifecyclePhase.Running && !acceptedMetadata || _acceptedRuns.Count >= 16384)
                throw new IOException("execution_queued_registration_blocked");
            var run = new AcceptedExecutionRun(this, owner, runId, []); run.BindQueue(operation, request);
            _acceptedRuns.Add(runId, run); return run;
        }
    }

    internal AcceptedExecutionRun? FindQueuedRun(ResourceLeaseManager owner, Guid runId, OperationIdentity operation, ExecutionSelectionRequest request)
    {
        lock (_gate)
        {
            if (!_acceptedRuns.TryGetValue(runId, out var run)) return null;
            if (!ReferenceEquals(run.ResourceOwner, owner) || run.QueuedSource != new QueuedExecutionSource(operation, ExecutionSelectionPolicy.FreezeRequest(request)))
                throw new IOException("execution_queued_source_mismatch");
            return run.IsQueued ? run : null;
        }
    }

    internal bool IsRunActive(AcceptedExecutionRun run)
    {
        lock (_gate) return _acceptedRuns.TryGetValue(run.RunId, out var current) && ReferenceEquals(current, run);
    }

    internal AcceptedExecutionRun? FindQueuedRun(ResourceLeaseManager owner, Guid runId, Guid operationId, string subject, QueueTarget target)
    {
        lock (_gate)
        {
            if (!_acceptedRuns.TryGetValue(runId, out var run)) return null;
            if (!ReferenceEquals(run.ResourceOwner, owner) || run.QueuedSource is not { } source ||
                source.Operation.OperationId != operationId || source.Operation.SubjectId != subject || source.Request.Target != target)
                throw new IOException("execution_queued_source_mismatch");
            return run.IsQueued ? run : null;
        }
    }

    internal void RefreshQueuedRun(ResourceLeaseManager owner, AcceptedExecutionRun run, OperationIdentity operation, ExecutionSelectionRequest request)
    {
        request = ExecutionSelectionPolicy.FreezeRequest(request);
        lock (_gate)
        {
            if (!ReferenceEquals(run.ResourceOwner, owner) || !IsRunActive(run) || !run.IsQueued ||
                run.QueuedSource is not { } source || operation.OperationId != source.Operation.OperationId ||
                operation.SubjectId != source.Operation.SubjectId || operation.Kind != source.Operation.Kind ||
                request.Target != source.Request.Target || request.ExpectedCatalogRevision < source.Request.ExpectedCatalogRevision ||
                request.ExpectedSettingsRevision < source.Request.ExpectedSettingsRevision || _phase == HostLifecyclePhase.ClosePermitted)
                throw new IOException("execution_queued_source_mismatch");
            run.BindQueue(operation, request);
        }
    }

    internal void RequireAcceptedContinuation(AcceptedExecutionRun run, Guid operationId, string subject, ExecutionSelectionRequest request)
    {
        request = ExecutionSelectionPolicy.FreezeRequest(request);
        lock (_gate)
        {
            if (!IsRunActive(run)) throw new IOException("execution_run_inactive");
            if (run.QueuedSource is { } queued && (run.IsQueued && operationId != queued.Operation.OperationId ||
                subject != queued.Operation.SubjectId || request != queued.Request)) throw new IOException("execution_queued_source_mismatch");
        }
    }

    internal void ReportUncertainOwnership(string reason) => recovery.BlockUnknown(reason);

    internal void ReleaseAcceptedRun(AcceptedExecutionRun run)
    {
        lock (_gate) if (IsRunActive(run)) _acceptedRuns.Remove(run.RunId);
    }

    internal AcceptedNotificationQueue RegisterNotificationQueue(ResourceLeaseManager owner, Guid operationId,
        AcceptedExecutionRun? origin)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("notification_queue_identity_required");
        lock (_gate)
        {
            if (_phase == HostLifecyclePhase.ClosePermitted ||
                (origin is not null ? !ReferenceEquals(origin.ResourceOwner, owner) || origin.IsQueued || !IsRunActive(origin) :
                    !_activeWork.Values.Any(work => work.OperationId == operationId && work.Claims.Any(claim =>
                        claim.ResourceKey == NotificationQueueResources.Queue && claim.Mode == ResourceClaimMode.Mutation))))
                throw new InvalidOperationException("notification_queue_origin_unbound");
            if (_notificationQueues.TryGetValue(operationId, out var existing))
            {
                if (!ReferenceEquals(existing.ResourceOwner, owner)) throw new InvalidOperationException("notification_queue_owner_unbound");
                return existing;
            }
            if (_notificationQueues.Count >= 64) throw new InvalidOperationException("notification_queue_owner_capacity");
            var queue = new AcceptedNotificationQueue(this, owner, operationId);
            _notificationQueues.Add(operationId, queue);
            return queue;
        }
    }

    internal IDisposable? TryEnterQueue(AcceptedNotificationQueue queue, ResourceLeaseManager owner,
        Guid operationId, IReadOnlyList<ResourceClaim> claims)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(queue.ResourceOwner, owner) || !IsQueueActive(queue) ||
                _phase == HostLifecyclePhase.ClosePermitted || !claims.SequenceEqual(NotificationQueueResources.SenderClaims)) return null;
            var verified = recovery.Current;
            if (!verified.IsReady || verified.Isolations.Any(item => Conflicts(claims, item.ResourceKey, item.Mode)) ||
                verified.ResourceHolds.Any(item => Conflicts(claims, item.ResourceKey, item.Mode))) return null;
            if (_phase == HostLifecyclePhase.Draining)
                _drainingOperations![operationId] = claims.ToDictionary(item => item.ResourceKey, item => item.Mode, StringComparer.Ordinal);
            return TrackWrite(operationId, claims);
        }
    }

    internal bool IsQueueActive(AcceptedNotificationQueue queue)
    {
        lock (_gate) return _notificationQueues.TryGetValue(queue.OperationId, out var actual) && ReferenceEquals(queue, actual);
    }

    internal void ReleaseNotificationQueue(AcceptedNotificationQueue queue)
    {
        lock (_gate) if (IsQueueActive(queue)) _notificationQueues.Remove(queue.OperationId);
    }

    private void FreezeDrain(RecoveryProjection verified)
    {
        if (_drainingContinuations is not null) return;
        _drainingContinuations = verified.ResourceHolds.Where(item => item.Owner.HasValue)
            .GroupBy(item => (item.Owner!.Value, item.OperationId))
            .ToDictionary(group => group.Key, group => group.GroupBy(item => item.ResourceKey, StringComparer.Ordinal)
                .ToDictionary(keys => keys.Key, keys => keys.Max(item => item.Mode), StringComparer.Ordinal));
        _drainingOperations = _activeWork.Values.GroupBy(item => item.OperationId).ToDictionary(group => group.Key,
            group => group.SelectMany(item => item.Claims).GroupBy(item => item.ResourceKey, StringComparer.Ordinal)
                .ToDictionary(keys => keys.Key, keys => keys.Max(item => item.Mode), StringComparer.Ordinal));
    }

    private bool AllowsDraining(RecoveryHoldOwner owner, Guid operationId, IReadOnlyList<ResourceClaim> claims)
    {
        bool Covered(Dictionary<string, ResourceClaimMode> accepted) => claims.All(claim =>
            accepted.TryGetValue(claim.ResourceKey, out var mode) && (claim.Mode == ResourceClaimMode.Reference || mode == ResourceClaimMode.Mutation));
        return _drainingContinuations!.TryGetValue((owner, operationId), out var held) && Covered(held) ||
            _drainingOperations!.TryGetValue(operationId, out var active) && Covered(active);
    }

    private IDisposable TrackWrite(Guid operationId, IReadOnlyList<ResourceClaim> claims)
    {
        var id = Guid.NewGuid();
        _activeWork.Add(id, new(operationId, Array.AsReadOnly(claims.ToArray())));
        return new WriteLease(this, id);
    }

    private sealed record AcceptedWrite(Guid OperationId, IReadOnlyList<ResourceClaim> Claims);

    private static bool Conflicts(IReadOnlyList<ResourceClaim> claims, string key, ResourceClaimMode held) =>
        claims.Any(claim => claim.ResourceKey == key && (claim.Mode == ResourceClaimMode.Mutation || held == ResourceClaimMode.Mutation));

    public bool CanFinalizeAfterExit(Guid sessionId)
    {
        lock (_gate)
        {
            var verified = recovery.Current;
            var permit = _permit;
            return _phase == HostLifecyclePhase.ClosePermitted && permit is not null && permit.SessionId == sessionId && _activeWork.Count == 0 && _acceptedRuns.Count == 0 && _notificationQueues.Count == 0 &&
                verified.IsReady && verified.Isolations.Count == 0 && verified.ResourceHolds.Count == 0 && verified.Revision == permit.RecoveryRevision;
        }
    }

    private void ReleaseWrite(Guid id)
    {
        lock (_gate)
        {
            if (!_activeWork.Remove(id)) throw new InvalidOperationException("Write lease accounting is inconsistent");
            if (_completionHostPayloadWrite == id) _completionHostPayloadWrite = null;
        }
    }

    private sealed class WriteLease(HostLifecycle owner, Guid id) : IDisposable
    {
        private HostLifecycle? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseWrite(id);
    }
}
