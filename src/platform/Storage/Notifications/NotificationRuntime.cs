using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
public sealed class NotificationRuntime : IAttemptTerminalObserver, IAsyncDisposable, IWorkQueueAcceptance
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _recovery;
    private readonly ExecutionRecordProjection _records;
    private readonly ExecutionNotificationRegistration _registration;
    private readonly NotificationDeliveryService _delivery;
    private readonly NotificationSender _sender;
    private readonly TaskTestService _tests;
    private readonly QueueActivityOwner _activity;
    private readonly NotificationRuntimeQueue _queue;
    private readonly CancellationTokenSource _stopping = new();
    private Task _worker = Task.CompletedTask;
    private Guid? _sendingAfter;
    private int _started, _disposed, _stepping;
    public string? FailureCode { get; private set; }
    internal bool HasPendingActivity => _activity.IsActive;

    public NotificationRuntime(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex recovery, SecretFieldService secrets)
        : this(instance, database, contracts, lease, resources, recovery, secrets, null) { }

    internal NotificationRuntime(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex recovery, SecretFieldService secrets, NotificationWebhookEndpoints? endpoints)
    {
        lease.VerifyHeld(instance);
        if (!resources.UsesRecoveryIndex(recovery)) throw new IOException("notification_recovery_owner_unbound");
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources; _recovery = recovery;
        var finality = new ExecutionRunFinality(instance, database, contracts);
        _records = new(instance, database, contracts, lease, finality);
        _registration = new(instance, database, contracts, lease, finality);
        _delivery = new(instance, database, contracts, lease);
        _sender = new(instance, database, contracts, lease, secrets, endpoints);
        _tests = new(instance, database, contracts, lease);
        _activity = new(resources); _queue = new(instance, database, contracts);
    }

    public void BindSchedules(NexusPipeline.Platform.Storage.Scheduling.PersistentScheduleService schedules)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(schedules);
        schedules.BindNotificationQueue(this);
    }
    IPreparedWorkQueue IWorkQueueAcceptance.Prepare(AcceptedExecutionRun actualOrigin)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var registration = _activity.BeginRegistration();
        try
        {
            _activity.Ensure(actualOrigin);
            return new WorkRegistration(this, actualOrigin, registration);
        }
        catch { registration.Dispose(); throw; }
    }
    private sealed class WorkRegistration(NotificationRuntime owner, AcceptedExecutionRun origin, IDisposable registration)
        : IPreparedWorkQueue
    {
        private BlobReference? _source;
        private int _disposed;
        public void Committed(BlobReference source)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
            owner._lease.VerifyHeld(owner._instance);
            if (!origin.IsActive || _source is not null && _source != source)
                throw new IOException("notification_scheduled_source_unbound");
            _ = NotificationScheduledSource.VerifyCommitted(owner._instance, owner._database, owner._contracts, source, origin.RunId);
            _source = source;
        }
        public void CommittedManual(BlobReference source)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
            owner._lease.VerifyHeld(owner._instance);
            if (!origin.IsActive || _source is not null && _source != source) throw NotificationManualCodec.Invalid();
            NotificationManualSource.VerifyCommitted(owner._instance, owner._database, owner._contracts, source, origin.RunId); _source = source;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) registration.Dispose();
        }
    }

    public void InitializeStartup()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var registering = _activity.BeginRegistration();
        try
        {
            while (_registration.ReconcilePending(64) == 64) { }
            if (!_queue.ReadVerifiedPending()) return;
            using var root = _resources.TryAcquire(Guid.NewGuid(), [new("notifications:queue", ResourceClaimMode.Mutation)]).Lease ??
                throw new IOException("notification_startup_queue_blocked");
            _activity.Ensure(root);
        }
        catch { BlockIntegrity(); throw; }
    }

    public void OnCommitted(AttemptReceipt receipt)
    {
        using var registering = _activity.BeginRegistration();
        _records.OnCommitted(receipt);
        _registration.OnCommitted(receipt);
        var status = _delivery.ReadPendingForRun(receipt.Facts.RunId);
        if (status.IsRegistered && !status.IsSettled)
            _activity.Ensure(_lease.RunBudgets.AcceptedForTerminal(_database, receipt));
    }

    internal TaskTestAcceptance EnqueueSavedTest(ResourceOperationLease root, Guid operation,
        string subject, long catalogRevision, Guid task, long taskRevision)
    {
        using var registering = _activity.BeginRegistration();
        var queue = _activity.Ensure(root);
        return _tests.EnqueueOwned(root, _resources, queue, operation, subject, catalogRevision, task, taskRevision);
    }

    public NotificationTestAcceptance EnqueueTaskTest(Guid operation, string subject,
        long expectedCatalogRevision, Guid taskId, long expectedTaskRevision)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _lease.VerifyHeld(_instance);
        var identity = TaskTestCodec.Identity(operation, subject,
            expectedCatalogRevision, taskId, expectedTaskRevision);
        var prior = _lease.PublishExecution(() => _tests.ReadExisting(identity));
        if (prior is not null) return new(operation, taskId, false);
        var admission = _resources.TryAcquire(operation, TaskTestService.AdmissionClaims);
        using var root = admission.Lease ?? throw new InvalidOperationException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "notification_resource_conflict" : "notification_host_unavailable");
        var accepted = EnqueueSavedTest(root, operation, subject, expectedCatalogRevision, taskId, expectedTaskRevision);
        return new(operation, taskId, accepted.IsNew);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("notification_runtime_already_started");
        _worker = RunAsync();
    }

    internal async Task RunPassAsync(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _stepping, 1, 0) != 0) throw new IOException("notification_worker_already_active");
        try
        {
            if (!_activity.IsActive) return;
            _sender.ReconcilePendingResults();
            using (var materialization = _activity.Continue(Guid.NewGuid()))
            {
                if (materialization is null) return;
                _delivery.MaterializePending(64);
            }
            var sending = _queue.Read("sending", _sendingAfter);
            foreach (var item in sending)
            {
                using var root = _activity.Continue(item.Operation);
                if (root is null) return;
                _sender.ReconcileStoppedOwner(root, _resources, item.Reference);
            }
            _sendingAfter = sending.Count == 4 ? sending[^1].Operation : null;
            foreach (var item in _queue.Read("queued"))
            {
                cancellation.ThrowIfCancellationRequested();
                using var root = _activity.Continue(item.Operation);
                if (root is null) return;
                await _sender.SendOwnedAsync(root, _resources, item.Reference, cancellation).ConfigureAwait(false);
            }
            TryReleaseSettled();
        }
        finally { Volatile.Write(ref _stepping, 0); }
    }

    internal bool TryReleaseSettled()
    {
        var epoch = _activity.ReadReleaseEpoch();
        if (epoch is null) return false;
        using var root = _activity.Continue(Guid.NewGuid());
        if (root is null || _queue.HasIndexedPending() || _queue.ReadVerifiedPending()) return false;
        return _activity.TryRelease(epoch.Value);
    }

    private async Task RunAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try { await RunPassAsync(_stopping.Token).ConfigureAwait(false); FailureCode = _sender.FailureCode; }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
            catch (SqliteException) { FailureCode = "notification_result_publication_pending"; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { BlockIntegrity(); return; }
            try { await Task.Delay(TimeSpan.FromSeconds(1), _stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
        }
    }

    private void BlockIntegrity()
    {
        FailureCode = "notification_runtime_integrity_unverified";
        _recovery.BlockUnknown(FailureCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        try { await _worker.ConfigureAwait(false); }
        finally
        {
            try { await _sender.DisposeAsync().ConfigureAwait(false); }
            finally { _activity.Dispose(); _stopping.Dispose(); }
        }
    }
}
