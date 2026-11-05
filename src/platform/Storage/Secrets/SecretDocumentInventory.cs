using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class SecretDocumentInventory(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal void VisitReferences(Action<BlobReference> include)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using (var exists = SecretDocumentRows.Command(connection, null, "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='secret_document_publications';"))
        {
            if (exists.ExecuteScalar() is null)
            {
                if (DatabaseSchema.VerifyCatalog(connection) == DatabaseSchema.Digest) throw SecretDocumentFormat.Invalid();
                return;
            }
        }
        var sources = new List<BlobReference>();
        using (var command = SecretDocumentRows.Command(connection, null, "SELECT relative_path,size_bytes,sha256 FROM secret_document_publications;"))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) sources.Add(new(rows.GetString(0), rows.GetInt64(1), rows.GetString(2)));
        foreach (var reference in sources)
        {
            var source = new SecretDocumentFormat(instance, contracts).Read(reference);
            _ = new SecretDocumentPublisher(instance, database, contracts).Status(source);
            include(source.Document.Content);
            if (source.Document.ProjectChoice is { } project) include(project.Manifest);
        }
    }

    internal void Verify()
    {
        var references = new List<(Guid OperationId, BlobReference Reference)>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = SecretDocumentRows.Command(connection, null,
            "SELECT operation_id,relative_path,size_bytes,sha256 FROM secret_document_publications;"))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) references.Add((Guid.ParseExact(rows.GetString(0), "D"), new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3))));
        var pending = new HashSet<(string, string)>();
        foreach (var item in references)
        {
            var source = new SecretDocumentFormat(instance, contracts).Read(item.Reference);
            if (source.Document.OperationId != item.OperationId) throw SecretDocumentFormat.Invalid();
            var status = new SecretDocumentPublisher(instance, database, contracts).Status(source);
            if (status == "accepted" && (!source.Document.Secrets.Any(change => change.Preparation is not null) ||
                !pending.Add((source.Document.ExtensionId, source.Document.Name)))) throw SecretDocumentFormat.Invalid();
            VerifyFields(source, status);
        }
    }

    private void VerifyFields(BoundSecretDocument source, string status)
    {
        var plans = source.Document.Secrets.Where(change => change.Preparation is not null).ToDictionary(change => change.OperationId,
            change => new PreparationPlanCodec(instance, contracts, database).Read(change.Preparation!).ReadWire());
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using (var command = SecretDocumentRows.Command(connection, transaction,
            "SELECT child_operation_id,subject_id,field FROM secret_document_fields WHERE parent_operation_id=$0;", source.Document.OperationId.ToString("D")))
        using (var rows = command.ExecuteReader())
        {
            var fields = new HashSet<(Guid, Guid, string)>();
            while (rows.Read()) fields.Add((Guid.ParseExact(rows.GetString(0), "D"), Guid.ParseExact(rows.GetString(1), "D"), rows.GetString(2)));
            if (fields.Count != source.Document.Secrets.Length || source.Document.Secrets.Any(change => !fields.Contains((change.OperationId, change.SubjectId, change.Field))))
                throw SecretDocumentFormat.Invalid();
        }
        foreach (var change in source.Document.Secrets)
        {
            var identity = SecretFieldStore.Identity(change.OperationId, new(source.Document.ExtensionId, change.SubjectId, change.Field), change.Before.Revision, change.Disposition);
            if (OperationRows.ReadExisting(connection, transaction, identity)?.Status != status) throw SecretDocumentFormat.Invalid();
            if (plans.TryGetValue(change.OperationId, out var plan))
            {
                SecretPreparationRows.VerifyPlan(connection, transaction, plan);
                var state = SecretPreparationRows.Read(connection, transaction, change.OperationId, change.ReplayProof);
                if (status == "completed" ? state != SecretPreparationState.Published : state is not (SecretPreparationState.Pending or SecretPreparationState.Quarantined))
                    throw SecretDocumentFormat.Invalid();
                var journal = RecoveryJournalRegistry.ReadInside(connection, transaction, change.JournalId!.Value);
                if (journal.OperationId != change.OperationId || journal.Kind != JournalKind.SecretPrepare ||
                    journal.PlanPath != change.Preparation!.RelativePath || journal.PlanDigest != change.Preparation.Sha256 ||
                    !journal.ResourceKeys.SequenceEqual(plan.ResourceKeys) || status == "completed" && journal.Phase != JournalPhase.Committed)
                    throw SecretDocumentFormat.Invalid();
            }
            else if (status == "accepted")
            {
                using var result = SecretDocumentRows.Command(connection, transaction, "SELECT 1 FROM secret_mutations WHERE operation_id=$0;", change.OperationId.ToString("D"));
                if (result.ExecuteScalar() is not null) throw SecretDocumentFormat.Invalid();
            }
        }
        transaction.Commit();
    }
}
