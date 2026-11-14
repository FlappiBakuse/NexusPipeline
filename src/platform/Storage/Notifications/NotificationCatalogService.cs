using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
public sealed class NotificationCatalogService
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _index;
    private readonly ContractReader _contracts;
    private readonly ISecretPreparationObserver? _observer;
    private readonly SecretOperationFlights _flights = new();
    private static readonly ResourceClaim[] Claims = [new("notifications:catalog", ResourceClaimMode.Mutation),
        new("secrets:fields", ResourceClaimMode.Mutation)];

    public NotificationCatalogService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex index, ContractReader contracts, ISecretPreparationObserver? observer = null)
    {
        lease.VerifyHeld(instance);
        _ = new SecretFieldStore(instance, database);
        if (!resources.UsesRecoveryIndex(index)) throw new IOException("notification_resource_index_unbound");
        _instance = instance; _database = database; _lease = lease; _resources = resources; _index = index;
        _contracts = contracts; _observer = observer;
    }

    public NotificationTaskCatalog Read()
    {
        _lease.VerifyHeld(_instance);
        NotificationCatalogCursor? cursor;
        using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications))
            cursor = NotificationCatalogRows.ReadCurrent(connection, null);
        if (cursor is null) return NotificationTaskCatalog.Empty;
        var saved = new NotificationCatalogCodec(_instance, _contracts).Read(cursor.Reference);
        if (saved.Catalog.Revision != cursor.Revision) throw new IOException("notification_catalog_cursor_unbound");
        return saved.Catalog;
    }

    public NotificationTargetCatalog ReadTargets()
    {
        _lease.VerifyHeld(_instance);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        var cursor = NotificationCatalogRows.ReadCurrent(connection, transaction);
        if (cursor is null) return new(0, []);
        var saved = new NotificationCatalogCodec(_instance, _contracts).Read(cursor.Reference);
        if (saved.Catalog.Revision != cursor.Revision) throw new IOException("notification_catalog_cursor_unbound");
        return new(saved.Catalog.Revision, saved.Catalog.Tasks.Select(task => new NotificationTargetState(task.Id, task.Revision,
            task.Name, task.IsEnabled, NotificationReadiness.Configured(task, field => SecretFieldRows.ReadField(connection, transaction,
                SecretFieldKey.NotificationTask(_instance.InstanceId, task.Id, field)).SecretId is not null), task.Kind)).ToImmutableArray());
    }

    public ValueTask<NotificationTaskCatalog> SaveAsync(Guid operationId, string subject, long expectedCatalogRevision, Guid taskId,
        long expectedTaskRevision, NotificationTaskInput input, IReadOnlyList<NotificationSecretEdit> secrets, CancellationToken cancellationToken = default) =>
        ApplyAsync(operationId, subject, new(NotificationCatalogChange.Save, expectedCatalogRevision, taskId, expectedTaskRevision,
            input, [], null), secrets, cancellationToken);

    public ValueTask<NotificationTaskCatalog> DeleteAsync(Guid operationId, string subject, long expectedCatalogRevision, Guid taskId,
        long expectedTaskRevision, CancellationToken cancellationToken = default) =>
        ApplyAsync(operationId, subject, new(NotificationCatalogChange.Delete, expectedCatalogRevision, taskId, expectedTaskRevision,
            null, [], null), [], cancellationToken);

    public ValueTask<NotificationTaskCatalog> ReorderAsync(Guid operationId, string subject, long expectedCatalogRevision,
        IReadOnlyList<Guid> order, CancellationToken cancellationToken = default) =>
        ApplyAsync(operationId, subject, new(NotificationCatalogChange.Reorder, expectedCatalogRevision, null, null,
            null, order.ToImmutableArray(), null), [], cancellationToken);

    public ValueTask<NotificationTaskCatalog> SetMaximumAsync(Guid operationId, string subject, long expectedCatalogRevision,
        int maximum, CancellationToken cancellationToken = default) =>
        ApplyAsync(operationId, subject, new(NotificationCatalogChange.Limit, expectedCatalogRevision, null, null,
            null, [], maximum), [], cancellationToken);

    private async ValueTask<NotificationTaskCatalog> ApplyAsync(Guid operationId, string subject, NotificationCatalogRequest request,
        IReadOnlyList<NotificationSecretEdit> secrets, CancellationToken cancellationToken)
    {
        _lease.VerifyHeld(_instance); cancellationToken.ThrowIfCancellationRequested();
        NotificationCatalogPreparation.RequireEdits(request, secrets);
        new NotificationPublicationInventory(_instance, _database, _contracts).VerifyReadOnly();
        _ = new OperationIdentity(operationId, subject, new string('0', 64), NotificationPublicationCodec.Kind(request.Change));
        using var flight = await _flights.EnterAsync(operationId, cancellationToken).ConfigureAwait(false);
        var replay = Lookup(operationId, subject, request, secrets);
        if (replay is not null) return replay;
        var admitted = _resources.TryAcquire(operationId, Claims);
        using var root = admitted.Lease ?? throw new InvalidOperationException(admitted.Denial == ResourceLeaseDenial.Conflict
            ? "notification_resource_conflict" : "notification_host_unavailable");
        using var prepared = new NotificationCatalogPreparation(_instance, _database, _contracts)
            .Freeze(root, _resources, operationId, subject, request, secrets);
        cancellationToken.ThrowIfCancellationRequested();
        using var cleanup = new SecretRetirementCleanup(_instance, _database, _contracts, _resources,
            prepared.Publication.Document.Secrets.Where(change => change.Retirement?.Plan is not null)
                .Select(change => change.Retirement!.Plan!));
        _index.BlockUnknown("notification_publication_in_progress");
        try
        {
            if (!new NotificationCatalogAcceptance(_instance, _database, _contracts).Accept(prepared, DateTimeOffset.UtcNow))
                return Lookup(operationId, subject, request, secrets) ?? throw new IOException("notification_original_result_missing");
            foreach (var payload in prepared.Payloads)
            {
                _observer?.Reached(SecretPreparationPoint.Accepted, Guid.Parse(payload.Plan.ReadWire().JournalId));
                _ = new SecretPreparationExecutor(_instance, _contracts, _database, _observer).WriteAcceptedCandidate(payload);
            }
            new SecretPreparationPublication(_instance, _database).CloseNotification(prepared.Publication, _contracts);
            cleanup.Finish(_instance, _database, _contracts, _lease, _index);
            foreach (var payload in prepared.Payloads)
                _observer?.Reached(SecretPreparationPoint.Published, Guid.Parse(payload.Plan.ReadWire().JournalId));
            return prepared.Publication.After.Catalog;
        }
        finally { PublishRecovery(); }
    }

    private NotificationTaskCatalog? Lookup(Guid operationId, string subject, NotificationCatalogRequest request,
        IReadOnlyList<NotificationSecretEdit> secrets)
    {
        BlobReference reference;
        using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT o.subject_id,o.operation_kind,p.relative_path,p.size_bytes,p.sha256 " +
                "FROM main.operations o LEFT JOIN notifications.notification_publications p ON p.operation_id=o.operation_id WHERE o.operation_id=$id;";
            command.Parameters.AddWithValue("$id", operationId.ToString("D"));
            using var row = command.ExecuteReader();
            if (!row.Read()) return null;
            if (row.GetString(0) != subject || row.GetString(1) != NotificationPublicationCodec.Kind(request.Change)) throw new OperationConflictException();
            if (row.IsDBNull(2)) throw new IOException("notification_publication_missing");
            reference = new(row.GetString(2), row.GetInt64(3), row.GetString(4));
        }
        var saved = new NotificationPublicationCodec(_instance, _contracts).Read(reference);
        if (!NotificationCatalogCodec.Encode(saved.Document.Request).AsSpan().SequenceEqual(NotificationCatalogCodec.Encode(request)))
            throw new OperationConflictException();
        var source = new NotificationCatalogPreparation(_instance, _database, _contracts).ReadSecrets(request, secrets, operationId, saved);
        if (NotificationPublicationCodec.Digest(_instance.InstanceId, subject, request, source) != saved.Document.RequestDigest)
            throw new OperationConflictException();
        if (new NotificationPublicationInventory(_instance, _database, _contracts).VerifyOne(saved) != "completed")
            throw new IOException("notification_publication_pending");
        return saved.After.Catalog;
    }

    private void PublishRecovery()
    {
        try
        {
            new RecoveryCoordinator(new RecoveryJournalRegistry(_database),
                new JournalRecoveryEvidence(_instance, new(_instance, _contracts), _database), _index).VerifyAndPublish();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        { _index.BlockUnknown("notification_publication_reconciliation_required"); }
    }
}
