using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using CoreSubmission = NexusPipeline.Core.Notifications.NotificationSubmission;
using ExtensionManifest = NexusPipeline.Extensibility.Protocol.ExtensionManifest;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
public sealed class NotificationRuntime : IAttemptTerminalObserver, IAsyncDisposable, IWorkNotificationPorts
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
    private readonly object _wakeGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task _worker = Task.CompletedTask;
    private Guid? _sendingAfter;
    private bool _registrationPending;
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
        _activity = new(resources, Signal); _queue = new(instance, database, contracts);
    }

    public void BindSchedules(PersistentScheduleService schedules)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(schedules);
        schedules.BindNotificationPorts(this);
    }

    NotificationSubmissionPort IWorkNotificationPorts.CreateScheduled(ExtensionManifest manifest, ExtensionScope scope,
        Func<CoreSubmission, WorkNotificationOrigin> bind)
    {
        var backend = new ScheduledNotificationSubmission(_instance, _database, _contracts, _lease, bind, this);
        return new(manifest, scope, backend, backend, _contracts);
    }

    NotificationSubmissionPort IWorkNotificationPorts.CreateManual(ExtensionManifest manifest, ExtensionScope scope,
        Func<CoreSubmission, WorkNotificationOrigin> bind)
    {
        var backend = new ManualNotificationSubmission(_instance, _database, _contracts, _lease, bind, this);
        return new(manifest, scope, backend, backend, _contracts);
    }

    internal WorkRegistration PrepareWork(AcceptedExecutionRun actualOrigin)
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
    internal sealed class WorkRegistration(NotificationRuntime owner, AcceptedExecutionRun origin, IDisposable registration) : IDisposable
    {
        private BlobReference? _source;
        private int _disposed;
        internal void CommittedScheduled(BlobReference source)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
            owner._lease.VerifyHeld(owner._instance);
            if (!origin.IsActive || _source is not null && _source != source)
                throw new IOException("notification_scheduled_source_unbound");
            NotificationScheduledSource.VerifyCommitted(owner._instance, owner._database, owner._contracts, source, origin.RunId);
            _source = source;
        }
        internal void CommittedManual(BlobReference source)
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

    public void InitializeStartup(NotificationStartupState? verified = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var registering = _activity.BeginRegistration();
        try
        {
            verified?.Consume(_instance, _lease.StartupGeneration);
            _registrationPending = _registration.HasPending();
            if (!_registrationPending && !_queue.HasIndexedPending()) return;
            using var root = _resources.TryAcquire(Guid.NewGuid(), [new("notifications:queue", ResourceClaimMode.Mutation)]).Lease ??
                throw new IOException("notification_startup_queue_blocked");
            _activity.Ensure(root);
        }
        catch { BlockIntegrity(); throw; }
    }

    public void OnCommitted(AttemptReceipt receipt)
    {
        using var registering = _activity.BeginRegistration();
        using var reads = new NotificationAttemptReads(_instance, _database, _contracts, _lease);
        reads.HoldJournal(receipt.JournalId);
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
            using var reads = new NotificationAttemptReads(_instance, _database, _contracts, _lease);
            if (Volatile.Read(ref _registrationPending))
            {
                using var capture = reads.CaptureMetadata();
                using var registering = _activity.BeginRegistration();
                Volatile.Write(ref _registrationPending, _registration.ReconcilePending(64, cancellation) == 64);
            }
            _sender.ReconcilePendingResults(reads);
            using (var materialization = _activity.Continue(Guid.NewGuid()))
            {
                if (materialization is null) return;
                _delivery.MaterializePending(64, reads);
            }
            var sending = _queue.Read("sending", _sendingAfter, reads);
            foreach (var item in sending)
            {
                using var root = _activity.Continue(item.Operation);
                if (root is null) return;
                reads.HoldDelivery(item.Operation);
                _sender.ReconcileStoppedOwner(root, _resources, item.Reference, reads);
            }
            _sendingAfter = sending.Count == 4 ? sending[^1].Operation : null;
            foreach (var item in _queue.Read("queued", reads: reads))
            {
                cancellation.ThrowIfCancellationRequested();
                using var root = _activity.Continue(item.Operation);
                if (root is null) return;
                reads.HoldDelivery(item.Operation);
                await _sender.SendOwnedAsync(root, _resources, item.Reference, cancellation, reads).ConfigureAwait(false);
            }
            TryReleaseSettled();
        }
        finally { Volatile.Write(ref _stepping, 0); }
    }

    internal bool TryReleaseSettled()
    {
        if (Volatile.Read(ref _registrationPending)) return false;
        var epoch = _activity.ReadReleaseEpoch();
        if (epoch is null) return false;
        using var root = _activity.Continue(Guid.NewGuid());
        if (root is null || _queue.HasIndexedPending()) return false;
        _queue.VerifyProjection();
        return _activity.TryRelease(epoch.Value);
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        while (!_stopping.IsCancellationRequested)
        {
            try { await RunPassAsync(_stopping.Token).ConfigureAwait(false); FailureCode = _sender.FailureCode; }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
            catch (SqliteException) { FailureCode = "notification_result_publication_pending"; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { BlockIntegrity(); return; }
            try
            {
                if (_activity.IsActive) await Task.Delay(TimeSpan.FromSeconds(1), _stopping.Token).ConfigureAwait(false);
                else await _wake.WaitAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
        }
    }

    private void Signal()
    {
        lock (_wakeGate)
            if (Volatile.Read(ref _disposed) == 0 && _wake.CurrentCount == 0) _wake.Release();
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
            finally
            {
                _activity.Dispose(); _stopping.Dispose();
                lock (_wakeGate) _wake.Dispose();
            }
        }
    }
}

internal sealed class NotificationRuntimeQueue(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal bool HasIndexedPending()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM notifications.notification_delivery_pending) OR " +
            "EXISTS(SELECT 1 FROM notifications.notification_receipt_progress INDEXED BY ix_notification_receipt_pending WHERE next_ordinal<route_count);";
        return (long)command.ExecuteScalar()! != 0;
    }

    internal void VerifyProjection()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications);
        using var transaction = DatabaseConnections.BeginRead(connection);
        NotificationReferenceInventory.RequireNoOrphans(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM notifications.notification_deliveries d " +
            "LEFT JOIN notifications.notification_delivery_transitions t ON t.operation_id=d.operation_id AND " +
            "t.ordinal=(SELECT max(ordinal) FROM notifications.notification_delivery_transitions WHERE operation_id=d.operation_id) " +
            "LEFT JOIN notifications.notification_delivery_pending p ON p.operation_id=d.operation_id WHERE t.operation_id IS NULL OR " +
            "json_type(t.document_json,'$.state') IS NOT 'text' OR " +
            "json_extract(t.document_json,'$.state') NOT IN ('queued','sending','unknown','succeeded','failed','skipped') OR " +
            "(json_extract(t.document_json,'$.state') IN ('queued','sending','unknown') AND " +
            "(p.operation_id IS NULL OR p.state<>json_extract(t.document_json,'$.state'))) OR " +
            "(json_extract(t.document_json,'$.state') IN ('succeeded','failed','skipped') AND p.operation_id IS NOT NULL)) " +
            "OR EXISTS(SELECT 1 FROM notifications.notification_receipt_progress p WHERE p.next_ordinal<0 OR p.next_ordinal>p.route_count " +
            "OR p.next_ordinal<>(SELECT count(*) FROM notifications.notification_receipt_routes r WHERE r.operation_id=p.operation_id) " +
            "OR (p.next_ordinal>0 AND ((SELECT min(ordinal) FROM notifications.notification_receipt_routes r WHERE r.operation_id=p.operation_id)<>0 " +
            "OR (SELECT max(ordinal) FROM notifications.notification_receipt_routes r WHERE r.operation_id=p.operation_id)<>p.next_ordinal-1)));";
        if ((long)command.ExecuteScalar()! != 0) throw NotificationReceiptCodec.Invalid();
        transaction.Commit();
    }

    internal IReadOnlyList<(Guid Operation, BlobReference Reference)> Read(string state, Guid? after = null,
        NotificationAttemptReads? reads = null)
    {
        using var capture = reads?.CaptureMetadata();
        if (state is not ("queued" or "sending")) throw new ArgumentException("notification_pending_state_invalid");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var result = new List<(Guid Operation, BlobReference Reference)>();
        using (var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT p.operation_id,d.relative_path,d.size_bytes,d.sha256 FROM notifications.notification_delivery_pending p " +
            "JOIN notifications.notification_deliveries d ON d.operation_id=p.operation_id WHERE p.state=$0 AND p.operation_id>$1 " +
            "ORDER BY p.operation_id LIMIT 4;", state, after?.ToString("D") ?? ""))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) result.Add((Guid.ParseExact(rows.GetString(0), "D"), new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3))));
        foreach (var item in result)
        {
            reads?.HoldDelivery(item.Operation);
            var saved = new NotificationDeliveryEvidence(instance, database, contracts).ReadFromSnapshot(item.Reference, connection, transaction);
            if (saved.Document.OperationId != item.Operation ||
                NotificationDeliveryRows.ReadState(connection, transaction, item.Operation).State.ToString().ToLowerInvariant() != state)
                throw new IOException("notification_pending_source_unbound");
        }
        transaction.Commit(); return result;
    }
}
