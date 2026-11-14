using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal static class SecretDocumentBinding
{
    internal static void Structure(SecretPreparationPlan plan)
    {
        if (plan.DocumentPublication is not { } binding) return;
        if (plan.OwnerKind != "extension" || plan.NotificationPublication is not null ||
            !Guid.TryParseExact(binding.ParentOperationId, "D", out var parent) || parent == Guid.Empty ||
            Guid.Parse(plan.OperationId) != SecretDocumentFormat.Child(parent, Guid.Parse(plan.SubjectId), plan.Field) ||
            !FileEffectRows.IsDigest(binding.ParentRequestDigest) || binding.Ciphertext is not { } content ||
            content.RelativePath != $"blobs/content/{plan.Candidate.Sha256}.bin" || content.SizeBytes != plan.Candidate.SizeBytes ||
            content.Sha256 != plan.Candidate.Sha256) throw SecretDocumentFormat.Invalid();
    }

    internal static void Parent(SqliteConnection connection, SqliteTransaction transaction, SecretPreparationPlan plan)
    {
        if (plan.DocumentPublication is not { } binding) return;
        Structure(plan);
        using var command = SecretDocumentRows.Command(connection, transaction,
            "SELECT o.request_digest,o.operation_kind,o.status,p.extension_id,f.subject_id,f.field " +
            "FROM main.operations o JOIN system.secret_document_publications p ON p.operation_id=o.operation_id " +
            "JOIN system.secret_document_fields f ON f.parent_operation_id=p.operation_id WHERE o.operation_id=$0 AND f.child_operation_id=$1;",
            binding.ParentOperationId, plan.OperationId);
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != binding.ParentRequestDigest || row.GetString(1) != "extension.document.write" ||
            row.GetString(2) != "accepted" || row.GetString(3) != plan.ExtensionId || row.GetString(4) != plan.SubjectId || row.GetString(5) != plan.Field)
            throw SecretDocumentFormat.Invalid();
    }

    internal static BoundSecretDocument? Accepted(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        FrozenSecretPreparation frozen, BlobReference reference)
    {
        var plan = frozen.ReadWire(); Structure(plan);
        if (plan.DocumentPublication is not { } binding) return null;
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        var parentReference = SecretDocumentRows.Find(connection, transaction, Guid.Parse(binding.ParentOperationId)) ?? throw SecretDocumentFormat.Invalid();
        var parent = new SecretDocumentFormat(instance, contracts).Read(parentReference);
        _ = SecretDocumentRows.Status(connection, transaction, parent);
        var change = parent.Document.Secrets.SingleOrDefault(value => value.OperationId.ToString("D") == plan.OperationId)
            ?? throw SecretDocumentFormat.Invalid();
        if (parent.Document.RequestDigest != binding.ParentRequestDigest || parent.Document.ExtensionId != plan.ExtensionId ||
            change.SubjectId.ToString("D") != plan.SubjectId || change.Field != plan.Field || change.Disposition != Core.Secrets.SecretDisposition.Set ||
            change.Before.Revision != plan.ExpectedRevision || change.Before.SecretId?.ToString("D") != plan.BeforeSecretId ||
            change.After.SecretId?.ToString("D") != plan.Candidate.SecretId || change.JournalId?.ToString("D") != plan.JournalId ||
            change.Preparation != reference || change.ReplayProof != plan.ReplayProof || change.Retirement != plan.Retirement &&
            System.Text.Json.JsonSerializer.Serialize(change.Retirement ?? new() { State = "none" }) != System.Text.Json.JsonSerializer.Serialize(plan.Retirement))
            throw SecretDocumentFormat.Invalid();
        transaction.Commit(); return parent;
    }
}
