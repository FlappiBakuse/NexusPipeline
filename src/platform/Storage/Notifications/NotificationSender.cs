using System.Runtime.Versioning;
using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class NotificationSender : IAsyncDisposable
{
    internal static ImmutableArray<ResourceClaim> Claims { get; } = [
        new("notifications:catalog", ResourceClaimMode.Reference), new("secrets:fields", ResourceClaimMode.Reference),
        new("settings:host", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)];
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly SecretFieldService _secrets;
    private readonly NotificationWebhookEndpoints _endpoints;
    private readonly NotificationSendingStore _store;
    private readonly Func<MailKit.Net.Smtp.SmtpClient>? _smtpClientFactory;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TaskCompletionSource> _active = [];
    private readonly Dictionary<Guid, PendingResult> _pending = [];
    private readonly CancellationTokenSource _stopping = new();
    private bool _closed;
    private int _disposeStarted, _disposeCompleted;
    private readonly TaskCompletionSource _disposeFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string? FailureCode { get; private set; }

    internal NotificationSender(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        SecretFieldService secrets, NotificationWebhookEndpoints? endpoints = null,
        Func<MailKit.Net.Smtp.SmtpClient>? smtpClientFactory = null)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "runtime.db")))
            throw new IOException("notification_sender_instance_unbound");
        _instance = instance; _database = database; _contracts = contracts; _lease = lease;
        _secrets = secrets; _smtpClientFactory = smtpClientFactory; _endpoints = endpoints ?? NotificationWebhookEndpoints.Production;
        _store = new(instance, database, contracts, lease);
    }

    internal async Task<NotificationDeliveryState> SendOwnedAsync(ResourceOperationLease actualSenderRoot,
        ResourceLeaseManager expectedOwner, BlobReference reference, CancellationToken cancellation = default)
    {
        _lease.VerifyHeld(_instance);
        var snapshot = new NotificationDeliveryEvidence(_instance, _database, _contracts).Read(reference);
        if (NotificationDeliveryOrigins.Reference(snapshot.Document) is null) throw new IOException("notification_delivery_registration_required");
        var id = snapshot.Document.OperationId;
        var completed = Enter(id);
        ResourceOperationBorrow? borrow = null;
        try
        {
            borrow = actualSenderRoot.Borrow(expectedOwner, id, Claims);
            borrow.VerifyHeld();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopping.Token);
            var catalog = new NotificationCatalogCodec(_instance, _contracts).Read(snapshot.Document.Catalog);
            var task = catalog.Catalog.Tasks.Single(item => item.Id == snapshot.Document.Task.TaskId);
            timeout.CancelAfter(TimeSpan.FromSeconds(task.Configuration.TimeoutSeconds));
            NotificationDeliveryNetwork network;
            using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                var state = NotificationDeliveryRows.ReadState(connection, transaction, id);
                if (state.State != NotificationDeliveryState.Queued) return state.State;
                network = DeliveryNetworkEvidence.CaptureCurrent(connection, transaction, _instance.InstanceId);
                transaction.Commit();
            }
            timeout.Token.ThrowIfCancellationRequested();
            var claimed = _store.Claim(snapshot, network);
            NotificationTransportResult outcome;
            var dispatched = false;
            try
            {
                using var preparedNetwork = PreparedNotificationNetwork.Capture(_instance, _database,
                    actualSenderRoot, id, _secrets, network);
                using var preparedSecrets = PreparedDeliverySecrets.Acquire(_instance, _database,
                    _lease, _contracts, actualSenderRoot, expectedOwner, reference);
                using var material = NotificationSecretMaterial.Capture(preparedSecrets);
                using var attachment = NotificationAttachment.Open(_instance, _database, _contracts, snapshot.Document.Attachment);
                borrow.VerifyHeld(); _lease.VerifyHeld(_instance); timeout.Token.ThrowIfCancellationRequested();
                if (task.Kind == NotificationTaskKind.Smtp)
                    outcome = await NotificationSmtpTransport.SendAsync(snapshot, task, material, attachment, timeout.Token,
                        _smtpClientFactory, () => dispatched = true).ConfigureAwait(false);
                else
                {
                    dispatched = true;
                    outcome = task.Kind switch {
                    NotificationTaskKind.Webhook => await new NotificationWebhookTransport(_endpoints).SendAsync(snapshot, task,
                        material, preparedNetwork, attachment, timeout.Token).ConfigureAwait(false),
                    _ => throw new IOException("notification_task_kind_invalid") };
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { outcome = new(dispatched ? NotificationTransportOutcome.Unknown : NotificationTransportOutcome.Rejected, null); }
            try
            {
                _store.Publish(snapshot, claimed, outcome);
                return outcome.State;
            }
            catch (SqliteException)
            {
                lock (_gate) _pending.Add(id, new(snapshot, claimed, outcome, borrow));
                borrow = null; FailureCode = "notification_result_publication_pending";
                return NotificationDeliveryState.Sending;
            }
        }
        finally
        {
            borrow?.Dispose();
            lock (_gate) _active.Remove(id);
            completed.TrySetResult();
        }
    }

    internal int ReconcilePendingResults(NotificationAttemptReads? reads = null)
    {
        _lease.VerifyHeld(_instance);
        PendingResult[] saved; lock (_gate) saved = _pending.Values.ToArray();
        foreach (var item in saved)
        {
            item.Borrow.VerifyHeld();
            reads?.HoldDelivery(item.Snapshot.Document.OperationId);
            _store.Publish(item.Snapshot, item.Claimed, item.Result);
            lock (_gate) _pending.Remove(item.Snapshot.Document.OperationId);
            item.Borrow.Dispose();
        }
        lock (_gate) { if (_pending.Count == 0) FailureCode = null; }
        return saved.Length;
    }

    internal bool ReconcileStoppedOwner(ResourceOperationLease actualSenderRoot, ResourceLeaseManager expectedOwner, BlobReference reference)
    {
        var snapshot = new NotificationDeliveryEvidence(_instance, _database, _contracts).Read(reference);
        if (NotificationDeliveryOrigins.Reference(snapshot.Document) is null) throw new IOException("notification_delivery_registration_required");
        using var borrow = actualSenderRoot.Borrow(expectedOwner, snapshot.Document.OperationId, Claims);
        return borrow.Use(() => _store.ReconcileStoppedOwner(reference));
    }

    internal IReadOnlyList<BlobReference> ReadQueuedForSource(BlobReference source, Guid? after = null, int maximum = 4)
        => ReadForSource(source, "queued", after, maximum);

    internal IReadOnlyList<BlobReference> ReadSendingForSource(BlobReference source, Guid? after = null, int maximum = 4)
        => ReadForSource(source, "sending", after, maximum);

    private IReadOnlyList<BlobReference> ReadForSource(BlobReference source, string requiredState, Guid? after, int maximum)
    {
        if (maximum is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maximum));
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        var sourceOperation = NotificationDeliveryOrigins.ReadOperation(_instance, _database, _contracts, source, connection, transaction);
        var result = new List<BlobReference>();
        using (var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT d.relative_path,d.size_bytes,d.sha256 FROM notifications.notification_delivery_pending p " +
            "JOIN notifications.notification_deliveries d ON d.operation_id=p.operation_id " +
            "WHERE p.state=$3 AND p.operation_id>$0 AND p.source_operation_id=$1 ORDER BY p.operation_id LIMIT $2;",
            after?.ToString("D") ?? "", sourceOperation.ToString("D"), maximum, requiredState))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetInt64(1), rows.GetString(2)));
        foreach (var reference in result)
        {
            var saved = new NotificationDeliveryEvidence(_instance, _database, _contracts).ReadFromSnapshot(reference, connection, transaction);
            if (NotificationDeliveryOrigins.Reference(saved.Document) != source ||
                NotificationDeliveryRows.ReadState(connection, transaction, saved.Document.OperationId).State.ToString().ToLowerInvariant() != requiredState)
                throw new IOException("notification_delivery_source_unbound");
        }
        transaction.Commit(); return result;
    }

    internal async Task StopAsync()
    {
        if (Volatile.Read(ref _disposeCompleted) != 0) return;
        Task[] pending;
        lock (_gate) { _closed = true; pending = _active.Values.Select(value => value.Task).ToArray(); }
        try { _stopping.Cancel(); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeStarted) != 0) { }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        { await _disposeFinished.Task.ConfigureAwait(false); return; }
        try
        {
            await StopAsync().ConfigureAwait(false);
            PendingResult[] pending;
            lock (_gate) { pending = _pending.Values.ToArray(); _pending.Clear(); }
            foreach (var item in pending) item.Borrow.Dispose();
            _stopping.Dispose(); Volatile.Write(ref _disposeCompleted, 1);
            _disposeFinished.TrySetResult();
        }
        catch (Exception error) { _disposeFinished.TrySetException(error); throw; }
    }

    private TaskCompletionSource Enter(Guid id)
    {
        lock (_gate)
        {
            if (_closed) throw new ObjectDisposedException(nameof(NotificationSender));
            if (_active.ContainsKey(id) || _pending.ContainsKey(id)) throw new IOException("notification_delivery_in_progress");
            if (_active.Count + _pending.Count >= 4) throw new IOException("notification_sender_capacity");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _active.Add(id, completion); return completion;
        }
    }

    private sealed record PendingResult(NotificationDeliverySnapshot Snapshot, NotificationDeliveryTransition Claimed,
        NotificationTransportResult Result, ResourceOperationBorrow Borrow);
}
