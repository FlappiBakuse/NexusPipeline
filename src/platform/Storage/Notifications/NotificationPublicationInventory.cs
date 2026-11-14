using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class NotificationPublicationInventory(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal void VerifyReadOnly()
    {
        var references = new List<(Guid OperationId, BlobReference Reference, long Before, long After)>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT operation_id,relative_path,size_bytes,sha256,before_revision,after_revision FROM notifications.notification_publications;";
            using var rows = command.ExecuteReader();
            while (rows.Read()) references.Add((Guid.ParseExact(rows.GetString(0), "D"),
                new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3)), rows.GetInt64(4), rows.GetInt64(5)));
        }
        var published = new List<NotificationPublicationSnapshot>();
        NotificationPublicationSnapshot? pending = null;
        foreach (var row in references)
        {
            var publication = new NotificationPublicationCodec(instance, contracts).Read(row.Reference);
            if (publication.Document.OperationId != row.OperationId || publication.Before.Catalog.Revision != row.Before ||
                publication.After.Catalog.Revision != row.After) throw new IOException("notification_publication_row_unbound");
            var status = VerifyOne(publication);
            if (status == "completed") published.Add(publication);
            else
            {
                if (pending is not null) throw new IOException("notification_multiple_pending_publications");
                pending = publication;
            }
        }
        var ordered = published.OrderBy(item => item.After.Catalog.Revision).ToArray();
        NotificationPublicationSnapshot? previous = null;
        for (var index = 0; index < ordered.Length; index++)
        {
            var next = ordered[index];
            if (next.Before.Catalog.Revision != index || next.After.Catalog.Revision != index + 1 ||
                previous is not null && next.Before.Reference != previous.After.Reference ||
                previous is null && (next.Before.Catalog.Tasks.Length != 0 || next.Before.Catalog.MaximumTasks != 10))
                throw new IOException("notification_catalog_history_unbound");
            previous = next;
        }
        using var currentConnection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System);
        var cursor = NotificationCatalogRows.ReadCurrent(currentConnection, null);
        if ((previous is null) != (cursor is null) || previous is not null &&
            (cursor!.Reference != previous.After.Reference || cursor.Revision != previous.After.Catalog.Revision) ||
            pending is not null && (pending.Before.Catalog.Revision != ordered.Length ||
                previous is not null && pending.Before.Reference != previous.After.Reference))
            throw new IOException("notification_catalog_current_unbound");
        NotificationCatalogRows.RequireTaskEntries(currentConnection, null,
            previous?.After.Document.Tasks ?? System.Collections.Immutable.ImmutableArray<NotificationCatalogEntry>.Empty);
        using var unknown = currentConnection.CreateCommand();
        unknown.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operations o WHERE o.operation_kind LIKE 'notification.%' AND o.operation_kind NOT IN ('notification.delivery','notification.registration','notification.saved-test') AND NOT EXISTS " +
            "(SELECT 1 FROM notifications.notification_publications p WHERE p.operation_id=o.operation_id)) OR " +
            "EXISTS(SELECT 1 FROM main.operations o WHERE o.subject_id LIKE 'notificationTask/%' AND NOT EXISTS " +
            "(SELECT 1 FROM notifications.notification_publication_fields f WHERE f.child_operation_id=o.operation_id)) OR " +
            "EXISTS(SELECT 1 FROM notifications.notification_task_ids t JOIN notifications.notification_publications p ON p.operation_id=t.first_operation_id " +
            "LEFT JOIN notifications.notification_publication_outcomes r ON r.operation_id=p.operation_id WHERE r.outcome IS NULL OR r.outcome!='published');";
        if ((long)unknown.ExecuteScalar()! != 0) throw new IOException("notification_publication_inventory_unknown");
    }

    internal string VerifyOne(NotificationPublicationSnapshot publication)
    {
        var document = publication.Document;
        var childPlans = document.Secrets.Where(field => field.Disposition == SecretDisposition.Set).ToDictionary(field => field.Field,
            field => new PreparationPlanCodec(instance, contracts, database).Read(field.Preparation!).ReadWire());
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
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
        transaction.Commit();
        return accepted.Status;
    }
}
