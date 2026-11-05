using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
public sealed class CompletionActionService : IAttemptTerminalObserver, ICompletionActionEffects
{
    private readonly CompletionCandidateStore _candidates;
    private readonly CompletionActionStore _store;
    private readonly CompletionActionEffects _effects;
    private readonly RecoveryIndex _recovery;
    private readonly TimeProvider _clock;
    private readonly ICompletionLocalAuthorization _authorization;
    private readonly Channel<byte> _pulse = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly Guid _failureOperation = Guid.NewGuid();
    private CancellationTokenSource? _countdown;
    private Guid? _countdownOperation;
    private Task _loop = Task.CompletedTask;
    private int _started;
    public string? FailureCode { get; private set; }

    public CompletionActionService(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        HostLifecycle lifecycle, ResourceLeaseManager resources, RecoveryIndex recovery, ExecutionRunFinality finality, ICompletionLocalAuthorization authorization,
        IHostCompletionExit hostExit, TimeProvider? clock = null)
        : this(instance, database, contracts, lease, lifecycle, resources, recovery, finality, authorization, hostExit, new WindowsCompletionCalls(), clock ?? TimeProvider.System) { }

    internal CompletionActionService(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        HostLifecycle lifecycle, ResourceLeaseManager resources, RecoveryIndex recovery, ExecutionRunFinality finality, ICompletionLocalAuthorization authorization,
        IHostCompletionExit hostExit, IWindowsCompletionCalls native, TimeProvider clock)
    {
        lease.VerifyHeld(instance);
        if (!lifecycle.UsesRecoveryIndex(recovery) || !resources.UsesRecoveryIndex(recovery) || database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw CompletionActionCodec.Invalid();
        var source = new CompletionIntentSource(instance, database, contracts, lease, finality);
        CompletionActionStartup.Verify(instance, database, lease, source);
        _clock = clock; _recovery = recovery; _authorization = authorization;
        _candidates = new(database, lease, source, Guid.NewGuid(), clock);
        _store = new(database, lease, _candidates, source, authorization, clock, new(database, lease, resources, lifecycle));
        _effects = new(_store, native, hostExit, clock);
        CompletionActionStartup.ReconcileStoppedOwner(database, lease, recovery);
        _candidates.RetirePriorCandidates();
    }

    public void OnCommitted(AttemptReceipt receipt)
    {
        if (_stopping.IsCancellationRequested || FailureCode is not null) return;
        try
        {
            _authorization.ObserveTerminal(receipt);
            if (receipt.Facts.Lifecycle != LifecycleOutcome.Completed) return;
            if (_candidates.Accept(receipt) is null) return;
            _store.BeginPending(); _pulse.Writer.TryWrite(0);
        }
        catch (Exception error) when (IsMetadataFailure(error))
        { ReportFailure("completion_registration_failed"); }
    }

    public Task RunAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("completion_already_started");
        return _loop = LoopAsync();
    }

    public CompletionActionSnapshot? Read(Guid operationId) => _store.Read(operationId, CompletionActionStore.HostSubject);
    public Guid? PendingOperationId => _store.PendingOperationId;
    public Task CancelLocalAsync(Guid operationId, CancellationToken cancellation)
        => CancelAsync(operationId, CompletionActionStore.HostSubject, cancellation);

    public Task ExecuteAsync(IPreparedCompletionAction action, CancellationToken cancellation)
        => _effects.ExecuteAsync(action, cancellation);

    public Task CancelAsync(Guid operationId, string subject, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var cancelled = _store.Cancel(operationId, subject);
        if (!cancelled && _store.Read(operationId, subject)?.Phase != CompletionEffectPhase.Cancelled)
            throw new InvalidOperationException("completion_not_cancellable");
        lock (_gate) if (_countdownOperation == operationId) _countdown?.Cancel();
        return Task.CompletedTask;
    }

    public bool RevokeLocalAuthorization()
    {
        _authorization.Revoke();
        var operation = _store.PendingOperationId;
        var cancelled = operation is not null && _store.Cancel(operation.Value, CompletionActionStore.HostSubject, ownerDisposing: true);
        lock (_gate) _countdown?.Cancel();
        return cancelled;
    }

    public async Task StopAsync()
    {
        _stopping.Cancel();
        lock (_gate) _countdown?.Cancel();
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        if (_store.PendingOperationId is { } operation)
            _store.Cancel(operation, CompletionActionStore.HostSubject, ownerDisposing: true);
    }

    private async Task LoopAsync()
    {
        try
        {
            while (await _pulse.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                while (_pulse.Reader.TryRead(out _)) { }
                while (!_stopping.IsCancellationRequested && FailureCode is null)
                {
                    _store.BeginPending();
                    if (_store.PendingOperationId is not { } operation)
                    {
                        if (!_candidates.HasPending()) break;
                        await Task.Delay(TimeSpan.FromSeconds(1), _clock, _stopping.Token).ConfigureAwait(false); continue;
                    }
                    if (_store.HasEnteredOrFinished) break;
                    if (!_store.IsReadyToPrepare())
                    { await Task.Delay(TimeSpan.FromMilliseconds(100), _clock, _stopping.Token).ConfigureAwait(false); continue; }
                    var pending = _candidates.ReadPending();
                    using var countdown = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    lock (_gate) { _countdown = countdown; _countdownOperation = operation; }
                    CompletionAuthorizationContext? authorizationContext = null;
                    try
                    {
                        CompletionActionGroup group;
                        try { group = CompletionActionPolicy.Combine(pending.Select(item => item.Intent).ToArray()); }
                        catch (InvalidOperationException error) when (error.Message == "completion_action_conflict")
                        {
                            using var rejected = _store.Prepare(operation, CompletionActionStore.HostSubject, pending.Select(item => item.Intent).ToArray());
                            break;
                        }
                        var context = authorizationContext = CompletionActionStore.Context(group, pending);
                        if (!await _authorization.RequestApprovalAsync(context, countdown.Token).ConfigureAwait(false))
                        { _store.RejectUnapproved(operation); break; }
                        using var prepared = _store.Prepare(operation, CompletionActionStore.HostSubject, pending.Select(item => item.Intent).ToArray());
                        if (prepared is null) break;
                        await ExecuteAsync(prepared, countdown.Token).ConfigureAwait(false);
                        if (prepared.Snapshot.Phase == CompletionEffectPhase.Unknown) ReportFailure("completion_native_outcome_unknown");
                    }
                    catch (OperationCanceledException) when (countdown.IsCancellationRequested) { }
                    finally
                    {
                        if (authorizationContext is not null) _authorization.Forget(authorizationContext);
                        lock (_gate) { _countdown = null; _countdownOperation = null; }
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception error) when (IsMetadataFailure(error))
        { ReportFailure("completion_reconciliation_required"); }
    }

    private void ReportFailure(string reason)
    {
        FailureCode = reason;
        var hold = new RecoveryResourceHold(_store.PendingOperationId ?? _failureOperation, "completion:state", reason);
        var existing = _recovery.Current.ResourceHolds.Where(item => item.Owner == RecoveryHoldOwner.CompletionActions)
            .Where(item => item.OperationId != hold.OperationId || item.ResourceKey != hold.ResourceKey).Append(hold).ToArray();
        _recovery.UpdateResourceHolds(RecoveryHoldOwner.CompletionActions, existing);
    }

    private static bool IsMetadataFailure(Exception error) => error is IOException or InvalidOperationException or JsonException or
        SqliteException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or KeyNotFoundException;
}
