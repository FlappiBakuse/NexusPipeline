using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class NotificationPublicationInventory(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal const DatabaseDomain Domains = DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System;

    internal void VerifyReadOnly()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: Domains);
        using var transaction = DatabaseConnections.BeginRead(connection);
        VerifyInside(connection, transaction);
        transaction.Commit();
    }

    internal (NotificationCatalogCursor? Cursor, bool HasPending) VerifyInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var history = DatabaseConnections.Command(connection, transaction, """
            SELECT operation_id,relative_path,size_bytes,sha256,before_revision,after_revision
            FROM notifications.notification_publications ORDER BY after_revision,operation_id;
            """);
        using var rows = history.ExecuteReader();
        var hasRows = rows.Read();
        using var namespaceLease = hasRows ? StableDirectoryLease.Acquire(Path.Combine(instance.RuntimeRoot, "blobs", "plans")) : null;
        using var protectedBlobs = BlobReadScope.Hold(instance);
        var codec = new NotificationPublicationCodec(instance, contracts);
        NotificationCatalogSnapshot? previous = null;
        NotificationCatalogSnapshot? pendingBefore = null;
        long revision = 0;
        while (hasRows)
        {
            var publication = codec.Read(new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3)),
                previous is null ? null : (previous, protectedBlobs));
            if (publication.Document.OperationId != Guid.ParseExact(rows.GetString(0), "D") ||
                publication.Before.Catalog.Revision != rows.GetInt64(4) || publication.After.Catalog.Revision != rows.GetInt64(5))
                throw new IOException("notification_publication_row_unbound");
            var status = VerifyOne(publication, connection, transaction);
            if (status == "completed")
            {
                if (publication.Before.Catalog.Revision != revision || publication.After.Catalog.Revision != revision + 1 ||
                    previous is not null && publication.Before.Reference != previous.Reference || previous is null &&
                    (publication.Before.Catalog.Tasks.Length != 0 || publication.Before.Catalog.MaximumTasks != 10))
                    throw new IOException("notification_catalog_history_unbound");
                previous = publication.After;
                revision++;
            }
            else
            {
                if (pendingBefore is not null) throw new IOException("notification_multiple_pending_publications");
                pendingBefore = publication.Before;
            }
            hasRows = rows.Read();
        }
        rows.Close();
        var cursor = NotificationCatalogRows.ReadCurrent(connection, transaction);
        if ((previous is null) != (cursor is null) || previous is not null &&
            (cursor!.Reference != previous.Reference || cursor.Revision != previous.Catalog.Revision) ||
            pendingBefore is not null && (pendingBefore.Catalog.Revision != revision ||
                previous is not null && pendingBefore.Reference != previous.Reference))
            throw new IOException("notification_catalog_current_unbound");
        NotificationCatalogRows.RequireTaskEntries(connection, transaction,
            previous?.Document.Tasks ?? System.Collections.Immutable.ImmutableArray<NotificationCatalogEntry>.Empty);
        using var unknown = DatabaseConnections.Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM main.operations o WHERE o.operation_kind LIKE 'notification.%'
                AND o.operation_kind NOT IN ('notification.delivery','notification.registration','notification.saved-test',$0,$1)
                AND NOT EXISTS (SELECT 1 FROM notifications.notification_publications p WHERE p.operation_id=o.operation_id))
            OR EXISTS(SELECT 1 FROM main.operations o WHERE o.operation_kind=$0 AND NOT EXISTS (
                SELECT 1 FROM notifications.notification_manual_sources s
                JOIN main.manual_work_runs m ON m.operation_id=s.invocation_operation_id
                WHERE s.operation_id=o.operation_id AND o.subject_id=$2 AND o.request_digest=s.sha256 AND o.status='completed'))
            OR EXISTS(SELECT 1 FROM main.operations o WHERE o.operation_kind=$1 AND NOT EXISTS (
                SELECT 1 FROM notifications.notification_scheduled_sources s
                JOIN main.scheduled_provider_occurrences c ON c.occurrence_id=s.occurrence_id
                WHERE s.operation_id=o.operation_id AND o.subject_id=$3 AND o.request_digest=s.sha256 AND o.status='completed'))
            OR EXISTS(SELECT 1 FROM notifications.notification_manual_sources s
                LEFT JOIN main.operations o ON o.operation_id=s.operation_id WHERE o.operation_kind IS NOT $0)
            OR EXISTS(SELECT 1 FROM notifications.notification_scheduled_sources s
                LEFT JOIN main.operations o ON o.operation_id=s.operation_id WHERE o.operation_kind IS NOT $1)
            OR EXISTS(SELECT 1 FROM main.operations o WHERE o.subject_id LIKE 'notificationTask/%' AND NOT EXISTS (
                SELECT 1 FROM notifications.notification_publication_fields f WHERE f.child_operation_id=o.operation_id))
            OR EXISTS(SELECT 1 FROM notifications.notification_task_ids t
                JOIN notifications.notification_publications p ON p.operation_id=t.first_operation_id
                LEFT JOIN notifications.notification_publication_outcomes r ON r.operation_id=p.operation_id
                WHERE r.outcome IS NULL OR r.outcome!='published');
            """, NotificationManualCodec.Kind, NotificationScheduledCodec.Kind, NotificationManualCodec.Subject, NotificationScheduledCodec.Subject);
        if ((long)unknown.ExecuteScalar()! != 0) throw new IOException("notification_publication_inventory_unknown");
        return (cursor, pendingBefore is not null);
    }

    internal NotificationCatalogCursor VerifyAppend(SqliteConnection connection, SqliteTransaction transaction,
        BlobReference reference, NotificationCatalogCursor? before)
    {
        var publication = new NotificationPublicationCodec(instance, contracts).Read(reference);
        if (publication.Document.Request.Change is not (NotificationCatalogChange.Limit or NotificationCatalogChange.Reorder) ||
            (before is null ? publication.Before.Catalog.Revision != 0 || publication.Before.Catalog.Tasks.Length != 0 ||
                publication.Before.Catalog.MaximumTasks != 10 : before.Revision != publication.Before.Catalog.Revision ||
                before.Reference != publication.Before.Reference))
            throw new IOException("notification_catalog_history_unbound");
        var after = new NotificationCatalogCursor(publication.After.Catalog.Revision, publication.After.Reference);
        using var row = DatabaseConnections.Command(connection, transaction, """
            SELECT 1 FROM notifications.notification_publications
            WHERE operation_id=$0 AND before_revision=$1 AND after_revision=$2;
            """, publication.Document.OperationId.ToString("D"), publication.Before.Catalog.Revision, after.Revision);
        if (row.ExecuteScalar() is null || VerifyOne(publication, connection, transaction) != "completed" ||
            NotificationCatalogRows.ReadCurrent(connection, transaction) != after)
            throw new IOException("notification_catalog_current_unbound");
        NotificationCatalogRows.RequireTaskEntries(connection, transaction, publication.After.Document.Tasks);
        return after;
    }

    internal string VerifyOne(NotificationPublicationSnapshot publication)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: Domains);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var status = VerifyOne(publication, connection, transaction);
        transaction.Commit();
        return status;
    }

    private string VerifyOne(NotificationPublicationSnapshot publication, SqliteConnection connection, SqliteTransaction transaction)
    {
        var document = publication.Document;
        var childPlans = document.Secrets.Where(field => field.Disposition == SecretDisposition.Set).ToDictionary(field => field.Field,
            field => new PreparationPlanCodec(instance, contracts, database).Read(field.Preparation!).ReadWire());
        var accepted = NotificationCatalogRows.Lookup(connection, transaction, NotificationPublicationCodec.Identity(document))
            ?? throw new IOException("notification_publication_missing");
        if (accepted.Reference != publication.Reference) throw new IOException("notification_publication_reference_changed");
        using (var outcomes = connection.CreateCommand())
        {
            outcomes.Transaction = transaction; outcomes.CommandText = "SELECT outcome,catalog_revision FROM notifications.notification_publication_outcomes WHERE operation_id=$id;";
            outcomes.Parameters.AddWithValue("$id", document.OperationId.ToString("D"));
            using var row = outcomes.ExecuteReader();
            var found = row.Read();
            if (accepted.Status == "completed" ? !found || row.GetString(0) != "published" || row.GetInt64(1) != publication.After.Catalog.Revision : found)
                throw new IOException("notification_publication_outcome_unbound");
        }
        using (var fields = connection.CreateCommand())
        {
            fields.Transaction = transaction; fields.CommandText = "SELECT field,child_operation_id FROM notifications.notification_publication_fields WHERE parent_operation_id=$id ORDER BY field;";
            fields.Parameters.AddWithValue("$id", document.OperationId.ToString("D"));
            using var rows = fields.ExecuteReader(); var actual = new Dictionary<string, string>(StringComparer.Ordinal);
            while (rows.Read()) actual.Add(rows.GetString(0), rows.GetString(1));
            if (actual.Count != document.Secrets.Length || document.Secrets.Any(item =>
                !actual.TryGetValue(NotificationSecretFields.Name(item.Field), out var child) || child != item.OperationId.ToString("D")))
                throw new IOException("notification_publication_field_membership_unbound");
        }
        foreach (var field in document.Secrets)
        {
            var key = SecretFieldKey.NotificationTask(instance.InstanceId, document.Request.TaskId!.Value, field.Field);
            var identity = SecretFieldStore.Identity(field.OperationId, key, field.Before.Revision, field.Disposition);
            var operation = OperationRows.ReadExisting(connection, transaction, identity) ?? throw new IOException("notification_child_operation_missing");
            if (operation.Status != accepted.Status) throw new IOException("notification_child_parent_status_unbound");
            if (field.Disposition == SecretDisposition.Set)
            {
                var plan = childPlans[field.Field];
                SecretPreparationRows.VerifyPlan(connection, transaction, plan);
                var state = SecretPreparationRows.Read(connection, transaction, field.OperationId, field.ReplayProof);
                if (accepted.Status == "completed" ? state != SecretPreparationState.Published :
                    state is not (SecretPreparationState.Pending or SecretPreparationState.Quarantined))
                    throw new IOException("notification_child_state_unbound");
                var registered = RecoveryJournalRegistry.ReadInside(connection, transaction, field.JournalId!.Value);
                if (registered.OperationId != field.OperationId || registered.Kind != JournalKind.SecretPrepare ||
                    registered.PlanPath != field.Preparation!.RelativePath || registered.PlanDigest != field.Preparation.Sha256 ||
                    !registered.ResourceKeys.SequenceEqual(plan.ResourceKeys) || accepted.Status == "completed" && registered.Phase != JournalPhase.Committed)
                    throw new IOException("notification_child_registration_unbound");
            }
            if (accepted.Status == "completed") NotificationCatalogRows.RequireHistoricalResult(connection, transaction, document, field);
            else if (field.Disposition != SecretDisposition.Set)
            {
                using var result = connection.CreateCommand(); result.Transaction = transaction;
                result.CommandText = "SELECT 1 FROM system.secret_mutations WHERE operation_id=$id;"; result.Parameters.AddWithValue("$id", field.OperationId.ToString("D"));
                if (result.ExecuteScalar() is not null) throw new IOException("notification_partial_field_publication");
            }
        }
        return accepted.Status;
    }
}
