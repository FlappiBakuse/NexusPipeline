using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class CompletionActionStore(StateDatabase database, InstanceLease lease,
    CompletionCandidateStore candidates, CompletionIntentSource source, ICompletionLocalAuthorization authorization, TimeProvider clock,
    CompletionSchedulingBarrier scheduling)
    : ICompletionActionStore
{
    internal const string HostSubject = "host:completion";
    private readonly object _gate = new();
    private HostCompletionDrain? _drain;
    private Reservation? _reservation;
    internal Guid? PendingOperationId { get { lock (_gate) return _drain?.OperationId; } }
    internal bool HasEnteredOrFinished { get { lock (_gate) return _reservation is not null &&
        _reservation.Current.Phase is not (CompletionEffectPhase.Prepared or CompletionEffectPhase.Countdown); } }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal void BeginPending()
    {
        lock (_gate)
        {
            if (_drain is not null || !candidates.HasPending()) return;
            _reservation = null;
            var decision = scheduling.Begin(Guid.NewGuid());
            _drain = decision.Drain;
            if (decision.Waiting) return;
            if (_drain is null)
            {
                var pending = candidates.ReadPending();
                if (pending.Count != 0) candidates.Reject(pending, "completion_lifecycle_unavailable");
            }
        }
    }

    public IPreparedCompletionAction? Prepare(Guid operationId, string subject, IReadOnlyList<FrozenCompletionIntent> intents)
    {
        if (subject != HostSubject) throw new UnauthorizedAccessException("completion_subject_mismatch");
        lock (_gate)
        {
            if (_drain is null || _drain.OperationId != operationId) throw new IOException("completion_reservation_unbound");
            if (_reservation is not null) return _reservation;
            if (!_drain.TryReserve()) return null;
            // Every accepted writer has released only after registering its final receipt.
            var pending = candidates.ReadPending();
            if (pending.Count == 0) { _drain.Dispose(); _drain = null; return null; }
            var sorted = pending.Select(item => item.Intent).OrderBy(item => item.RunId).ToArray();
            if (CompletionActionCodec.Encode(intents.OrderBy(item => item.RunId).ToArray()) != CompletionActionCodec.Encode(sorted))
                throw new IOException("completion_candidates_changed");
            CompletionActionGroup group;
            try { group = CompletionActionPolicy.Combine(sorted); }
            catch (InvalidOperationException error) when (error.Message == "completion_action_conflict")
            {
                candidates.Reject(pending, "completion_action_conflict"); _drain.Dispose(); _drain = null; return null;
            }
            var context = Context(group, pending);
            if (!authorization.IsApproved(context))
            {
                candidates.Reject(pending, "completion_local_authorization_required"); _drain.Dispose(); _drain = null; return null;
            }
            var now = clock.GetUtcNow();
            var initial = new CompletionActionSnapshot(operationId, subject, group, CompletionEffectPhase.Prepared, now, now, null);
            lease.PublishExecution(() => database.Write((connection, transaction) =>
            { CompletionActionRows.Insert(connection, transaction, initial); return true; }));
            var countdown = initial with { Phase = CompletionEffectPhase.Countdown,
                NotBeforeUtc = now.AddSeconds(group.Action == AutomationCompletionAction.Exit ? 0 : 60) };
            Append(initial, countdown);
            return _reservation = new(this, _drain, countdown, context, clock.GetTimestamp());
        }
    }

    internal void RejectUnapproved(Guid operationId)
    {
        lock (_gate)
        {
            if (_drain is null || _drain.OperationId != operationId) return;
            if (_reservation is not null) { Cancel(operationId, HostSubject, ownerDisposing: true); return; }
            var pending = candidates.ReadPending();
            if (pending.Count != 0) candidates.Reject(pending, "completion_local_authorization_required");
            _drain.Dispose(); _drain = null;
        }
    }

    public CompletionActionSnapshot? Read(Guid operationId, string subject)
    {
        lease.VerifyHeld(lease.Instance);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        return CompletionActionRows.Read(connection, transaction, operationId, subject);
    }

    internal bool IsReadyToPrepare()
    {
        lock (_gate) return _drain is not null && _reservation is null && _drain.TryReserve();
    }

    internal TimeSpan Remaining(IPreparedCompletionAction action)
    {
        lock (_gate)
        {
            var reservation = Require(action);
            var wait = reservation.Current.Frozen.Action == AutomationCompletionAction.Exit ? TimeSpan.Zero : TimeSpan.FromSeconds(60);
            var elapsed = clock.GetElapsedTime(reservation.CountdownTimestamp);
            if (elapsed < TimeSpan.Zero) throw new IOException("completion_clock_unverified");
            return elapsed >= wait ? TimeSpan.Zero : wait - elapsed;
        }
    }

    private CompletionActionSnapshot? EnterEffect(IPreparedCompletionAction action)
    {
        lock (_gate)
        {
            var reservation = Require(action);
            if (reservation.Current.Phase == CompletionEffectPhase.Cancelled) return null;
            if (reservation.Current.Phase != CompletionEffectPhase.Countdown || Remaining(action) != TimeSpan.Zero)
                throw new IOException("completion_countdown_pending");
            var pending = reservation.Current.Frozen.Intents;
            foreach (var intent in pending)
            {
                var receipt = new AttemptReceiptStore(database).Read(intent.AttemptId) ?? throw CompletionActionCodec.Invalid();
                var actual = source.Capture(receipt) ?? throw CompletionActionCodec.Invalid();
                if (actual.Intent != intent || actual.SubjectId != reservation.Authorization.Sources.Single(item => item.Intent == intent).SubjectId)
                    throw CompletionActionCodec.Invalid();
            }
            var entered = reservation.Current with { Phase = CompletionEffectPhase.Entered };
            Append(reservation.Current, entered); reservation.Current = entered;
            if (!reservation.Drain.TryEnterEffect())
            {
                var failed = entered with { Phase = CompletionEffectPhase.Failed, ReasonCode = "completion_recovery_changed" };
                Append(entered, failed); reservation.Current = failed; reservation.Drain.TryCancel(); _drain = null;
                throw new IOException("completion_recovery_changed");
            }
            return entered;
        }
    }

    internal (CompletionActionSnapshot Snapshot, CompletionNativeResult Result)? InvokeAuthorized(
        IPreparedCompletionAction action, CancellationToken cancellation, Func<CompletionActionSnapshot, CompletionNativeResult> effect)
    {
        lock (_gate)
        {
            var reservation = Require(action);
            try
            {
                return authorization.UseApproved<(CompletionActionSnapshot, CompletionNativeResult)?>(reservation.Authorization, () =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    var entered = EnterEffect(action);
                    return entered is null ? null : (entered, effect(entered));
                });
            }
            catch (UnauthorizedAccessException) when (reservation.Current.Phase == CompletionEffectPhase.Countdown)
            {
                Cancel(reservation.Current.OperationId, reservation.Current.SubjectId, ownerDisposing: true);
                return null;
            }
        }
    }

    internal static CompletionAuthorizationContext Context(CompletionActionGroup group, IReadOnlyList<BoundCompletionIntent> pending) =>
        CompletionAuthorizationContext.Create(group, pending.Select(item => new CompletionAuthorizationSource(item.Intent, item.SubjectId)).ToArray());

    internal void RecordResult(IPreparedCompletionAction action, CompletionEffectPhase phase, string reason)
    {
        if (phase is not (CompletionEffectPhase.Accepted or CompletionEffectPhase.Failed or CompletionEffectPhase.Unknown))
            throw new ArgumentException("completion_effect_result_invalid");
        lock (_gate)
        {
            var reservation = Require(action);
            var next = reservation.Current with { Phase = phase, ReasonCode = reason };
            Append(reservation.Current, next); reservation.Current = next;
            if (phase == CompletionEffectPhase.Failed) { reservation.Drain.ReleaseFinishedEffect(); _drain = null; }
        }
    }

    internal void ReleaseReturnedHibernation(IPreparedCompletionAction action)
    {
        lock (_gate)
        {
            var reservation = Require(action);
            if (reservation.Current.Phase != CompletionEffectPhase.Accepted || reservation.Current.Frozen.Action != AutomationCompletionAction.Hibernate)
                throw new IOException("completion_hibernation_return_unbound");
            if (!reservation.Drain.ReleaseFinishedEffect()) throw CompletionActionCodec.Invalid();
            _drain = null;
        }
    }

    internal bool Cancel(Guid operationId, string subject, bool ownerDisposing = false)
    {
        if (subject != HostSubject) throw new UnauthorizedAccessException("completion_subject_mismatch");
        lock (_gate)
        {
            if (_drain is null || _drain.OperationId != operationId) return false;
            if (_reservation is null)
            {
                var pending = candidates.ReadPending();
                if (!ownerDisposing && pending.Count != 0 && pending.All(item => item.Intent.Action == AutomationCompletionAction.Exit)) return false;
                if (pending.Count != 0) candidates.Reject(pending, "completion_cancelled");
                _drain.Dispose(); _drain = null; return true;
            }
            if (_reservation.Current.Phase != CompletionEffectPhase.Countdown) return false;
            if (!ownerDisposing && !CompletionActionPolicy.CanCancel(_reservation.Current.Frozen.Action)) return false;
            var cancelled = _reservation.Current with { Phase = CompletionEffectPhase.Cancelled, ReasonCode = "completion_cancelled" };
            Append(_reservation.Current, cancelled); _reservation.Current = cancelled;
            if (!_drain.TryCancel()) throw CompletionActionCodec.Invalid();
            _drain = null; return true;
        }
    }

    private void Append(CompletionActionSnapshot expected, CompletionActionSnapshot next)
        => lease.PublishExecution(() => database.Write((connection, transaction) =>
        { CompletionActionRows.Append(connection, transaction, expected, next); return true; }));

    private Reservation Require(IPreparedCompletionAction action)
    {
        if (action is not Reservation reservation || !ReferenceEquals(reservation.Owner, this) || !ReferenceEquals(_reservation, reservation) || reservation.Disposed)
            throw new IOException("completion_reservation_unbound");
        var saved = Read(reservation.Current.OperationId, reservation.Current.SubjectId) ?? throw CompletionActionCodec.Invalid();
        if (CompletionActionCodec.Encode(saved) != CompletionActionCodec.Encode(reservation.Current)) throw CompletionActionCodec.Invalid();
        return reservation;
    }

    private sealed class Reservation(CompletionActionStore owner, HostCompletionDrain drain,
        CompletionActionSnapshot snapshot, CompletionAuthorizationContext authorization, long countdownTimestamp) : IPreparedCompletionAction
    {
        internal CompletionActionStore Owner { get; } = owner;
        internal HostCompletionDrain Drain { get; } = drain;
        internal CompletionActionSnapshot Current { get; set; } = snapshot;
        internal CompletionAuthorizationContext Authorization { get; } = authorization;
        internal long CountdownTimestamp { get; } = countdownTimestamp;
        internal bool Disposed { get; private set; }
        public CompletionActionSnapshot Snapshot { get { lock (Owner._gate) return Current; } }
        public void Dispose()
        {
            lock (Owner._gate)
            {
                if (Disposed) return;
                if (Current.Phase == CompletionEffectPhase.Countdown) Owner.Cancel(Current.OperationId, Current.SubjectId, ownerDisposing: true);
                Disposed = true;
            }
        }
    }
}
