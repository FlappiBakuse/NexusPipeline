using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationDeliveryEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal NotificationDeliverySnapshot Read(BlobReference reference)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ReadFromSnapshot(reference, connection, transaction);
        transaction.Commit();
        return result;
    }

    internal NotificationDeliverySnapshot ReadFromSnapshot(BlobReference reference,
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("notification_delivery_instance_unbound");
        var snapshot = new NotificationDeliveryCodec(instance, contracts).Read(reference);
        var document = snapshot.Document;
        var catalog = new NotificationCatalogCodec(instance, contracts).Read(document.Catalog);
        if (catalog.Catalog.Revision != document.CatalogRevision || !catalog.Document.Tasks.Contains(document.Task))
            throw new IOException("notification_delivery_catalog_unbound");
        NotificationDeliveryRows.RequireSaved(connection, transaction, snapshot);
        NotificationDeliveryRows.RequireSource(connection, transaction, document);
        var operation = NotificationDeliveryRows.Identity(snapshot);
        var transition = NotificationDeliveryRows.ReadState(connection, transaction, document.OperationId);
        NotificationDeliveryRows.RequirePendingProjection(connection, transaction, transition, NotificationDeliveryOrigins.SourceOperation(document));
        if (transition.Network is { } network)
            DeliveryNetworkEvidence.RequireHistorical(connection, transaction, document.InstanceId, network);
        else if (NotificationDeliveryOrigins.Reference(document) is not null && transition.State is not (NotificationDeliveryState.Queued or NotificationDeliveryState.Skipped))
            throw new IOException("notification_delivery_network_unbound");
        var state = transition.State;
        using (var identity = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT subject_id,request_digest,operation_kind,status FROM operations WHERE operation_id=$0;",
            document.OperationId.ToString("D")))
        using (var row = identity.ExecuteReader())
            if (!row.Read() || row.GetString(0) != operation.SubjectId || row.GetString(1) != operation.RequestDigest ||
                row.GetString(2) != operation.Kind ||
                row.GetString(3) != (state is NotificationDeliveryState.Queued or NotificationDeliveryState.Sending
                    ? "accepted" : "completed"))
                throw new IOException("notification_delivery_operation_unbound");
        RequireReferenceRows(connection, transaction, document);
        new NotificationCatalogCodec(instance, contracts).RequirePublishedCursor(connection, transaction, catalog);
        if (document.Source.Kind == "executionReceipt") RequireExecutionSource(snapshot, connection, transaction);
        else new ExplicitSourceEvidence(instance, database, contracts).RequireDelivery(snapshot, connection, transaction);
        return snapshot;
    }

    internal ImmutableArray<BlobReference> ReadReferences(NotificationDeliverySnapshot snapshot,
        NotificationDeliveryState state)
    {
        var document = snapshot.Document;
        var catalog = new NotificationCatalogCodec(instance, contracts).Read(document.Catalog);
        var result = ImmutableArray.CreateBuilder<BlobReference>();
        result.Add(snapshot.Reference); result.Add(document.Catalog);
        if (document.Registration is { } registration) result.Add(registration);
        result.AddRange(catalog.Document.Tasks.Select(item => item.Definition));
        if (document.Source.Kind == "executionReceipt")
        { result.Add(document.Source.TotalPlan); result.Add(document.Source.DefinitionBundle); }
        else result.Add(document.Source.ExplicitSource!);
        if (!NotificationDeliveryStates.ReleasesReferences(state) && document.Attachment is { } asset)
            result.Add(new($"blobs/content/{asset.Sha256}.bin", asset.SizeBytes, asset.Sha256));
        return result.Distinct().ToImmutableArray();
    }

    private void RequireExecutionSource(NotificationDeliverySnapshot snapshot,
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var document = snapshot.Document;
        var receipt = document.Source.Receipt;
        var registered = RecoveryJournalRegistry.ReadInside(connection, transaction, receipt.JournalId);
        if (registered.Kind != JournalKind.RunAttempt || registered.Phase != JournalPhase.Committed)
            throw new IOException("notification_delivery_source_unbound");
        var planReference = AttemptReference(registered);
        var attemptBytes = ReadBytes(planReference);
        var attempt = contracts.Read<RunAttemptPlan>(attemptBytes, "run-attempt-plan.schema.json");
        var source = document.Source;
        if (attempt.JournalId != receipt.JournalId.ToString("D") || attempt.OperationId != receipt.OperationId.ToString("D") ||
            attempt.RunId != receipt.Facts.RunId.ToString("D") || attempt.AttemptId != receipt.Facts.AttemptId.ToString("D") ||
            attempt.AttemptOrdinal != receipt.Facts.Ordinal || attempt.ExecutionPlan.RelativePath != source.TotalPlan.RelativePath ||
            attempt.ExecutionPlan.SizeBytes != source.TotalPlan.SizeBytes || attempt.ExecutionPlan.Sha256 != source.TotalPlan.Sha256)
            throw new IOException("notification_delivery_source_unbound");
        var total = contracts.Read<ExecutionPlan>(ReadBytes(source.TotalPlan), "execution-plan.schema.json");
        if (total.RunId != attempt.RunId || total.InstanceId != instance.InstanceId.ToString("D") ||
            new BlobReference(total.DefinitionBundle.RelativePath, total.DefinitionBundle.SizeBytes, total.DefinitionBundle.Sha256) != source.DefinitionBundle)
            throw new IOException("notification_delivery_source_unbound");
        if (source.RouteKind == "queue" && total.QueueId != source.RouteId.ToString("D") ||
            source.RouteKind == "binding" && !total.Selection.Any(item => item.BindingId == source.RouteId.ToString("D")))
            throw new IOException("notification_delivery_route_unbound");
        _ = contracts.Read<AutomationBundle>(ReadBytes(source.DefinitionBundle), "automation-bundle.schema.json");
        if (document.Registration is not null)
            new ReceiptRouteCodec(instance, database, contracts).RequireOrigin(snapshot, connection, transaction);
    }

    private BlobReference AttemptReference(RegisteredJournal registered)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, registered.PlanPath));
        return new(registered.PlanPath, new FileInfo(path).Length, registered.PlanDigest);
    }

    private byte[] ReadBytes(BlobReference reference)
    {
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw new IOException("notification_delivery_metadata_limit");
        using var input = new ImmutableBlobStore(instance, contracts).OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }

    private static NotificationDeliveryState State(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction, Guid operationId) =>
        NotificationDeliveryRows.ReadState(connection, transaction, operationId).State;

    private static void RequireReferenceRows(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction, NotificationDeliveryDocument document)
    {
        using (var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT field,revision,secret_id FROM notification_delivery_secrets WHERE operation_id=$0 ORDER BY field;",
            document.OperationId.ToString("D")))
        using (var rows = command.ExecuteReader())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (rows.Read())
            {
                var field = document.Secrets.SingleOrDefault(item =>
                    NexusPipeline.Core.Secrets.NotificationSecretFields.Name(item.Field) == rows.GetString(0));
                if (field is null || !seen.Add(rows.GetString(0)) || field.Reference.Revision != rows.GetInt64(1) ||
                    field.Reference.SecretId?.ToString("D") != (rows.IsDBNull(2) ? null : rows.GetString(2)))
                    throw new IOException("notification_delivery_reference_unbound");
                NotificationDeliveryRows.RequirePayload(connection, transaction, field, allowRetired: true);
                NotificationCatalogRows.RequireFieldProvenance(connection, transaction, document.InstanceId, document.Task.TaskId,
                    NexusPipeline.Core.Secrets.NotificationSecretFields.Name(field.Field), field.Reference, document.CatalogRevision);
            }
            if (seen.Count != document.Secrets.Length) throw new IOException("notification_delivery_reference_unbound");
        }
        using var assets = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT run_id,digest,kind,size_bytes FROM notification_delivery_assets WHERE operation_id=$0;",
            document.OperationId.ToString("D"));
        using var row = assets.ExecuteReader();
        if (document.Attachment is { } expected)
        {
            if (!row.Read() || row.GetString(0) != expected.RunId.ToString("D") || row.GetString(1) != expected.Sha256 ||
                row.GetInt32(2) != (int)expected.Kind || row.GetInt64(3) != expected.SizeBytes)
                throw new IOException("notification_delivery_attachment_unbound");
        }
        if (row.Read()) throw new IOException("notification_delivery_attachment_unbound");
    }
}
