using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal static class NotificationSecretBinding
{
    internal static void RequireStructure(SecretPreparationPlan plan)
    {
        var binding = plan.NotificationPublication;
        if (plan.OwnerKind != "notificationTask")
        {
            if (binding is not null) throw new IOException("secret_notification_binding_unexpected");
            return;
        }
        if (binding is null || Guid.Parse(binding.ParentOperationId) == Guid.Empty || binding.ParentOperationId == plan.OperationId ||
            !FileEffectRows.IsDigest(binding.ParentRequestDigest) || binding.Ciphertext is not { } source ||
            source.RelativePath != $"blobs/content/{plan.Candidate.Sha256}.bin" ||
            source.Sha256 != plan.Candidate.Sha256 || source.SizeBytes != plan.Candidate.SizeBytes)
            throw new IOException("secret_notification_binding_invalid");
        var field = NotificationSecretFields.Parse(plan.Field);
        if (NotificationPublicationCodec.ChildOperation(Guid.Parse(binding.ParentOperationId), field).ToString("D") != plan.OperationId)
            throw new IOException("secret_notification_operation_unbound");
    }

    internal static void RequireParentInside(SqliteConnection connection, SqliteTransaction transaction, SecretPreparationPlan plan)
    {
        if (plan.NotificationPublication is not { } binding) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT o.request_digest,o.operation_kind,o.status,f.field FROM operations o " +
            "JOIN notification_publications p ON p.operation_id=o.operation_id " +
            "JOIN notification_publication_fields f ON f.parent_operation_id=p.operation_id " +
            "WHERE o.operation_id=$parent AND f.child_operation_id=$child;";
        command.Parameters.AddWithValue("$parent", binding.ParentOperationId);
        command.Parameters.AddWithValue("$child", plan.OperationId);
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != binding.ParentRequestDigest || row.GetString(1) != "notification.task.save" ||
            row.GetString(2) != "accepted" || row.GetString(3) != plan.Field)
            throw new IOException("secret_notification_parent_sql_unbound");
    }

    internal static NotificationPublicationSnapshot? RequireAccepted(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        FrozenSecretPreparation frozen, BlobReference reference)
    {
        var plan = frozen.ReadWire();
        RequireStructure(plan);
        if (plan.NotificationPublication is not { } binding) return null;
        BlobReference parent;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT p.relative_path,p.size_bytes,p.sha256,o.request_digest,o.operation_kind,f.field " +
                "FROM notification_publications p JOIN operations o ON o.operation_id=p.operation_id " +
                "JOIN notification_publication_fields f ON f.parent_operation_id=p.operation_id " +
                "WHERE p.operation_id=$parent AND f.child_operation_id=$child;";
            command.Parameters.AddWithValue("$parent", binding.ParentOperationId);
            command.Parameters.AddWithValue("$child", plan.OperationId);
            using var row = command.ExecuteReader();
            if (!row.Read() || row.GetString(3) != binding.ParentRequestDigest || row.GetString(4) != "notification.task.save" ||
                row.GetString(5) != plan.Field) throw new IOException("secret_notification_parent_sql_unbound");
            parent = new(row.GetString(0), row.GetInt64(1), row.GetString(2));
        }
        var saved = new NotificationPublicationCodec(instance, contracts).Read(parent);
        var field = saved.Document.Secrets.Single(item => NotificationSecretFields.Name(item.Field) == plan.Field);
        if (saved.Document.OperationId.ToString("D") != binding.ParentOperationId ||
            saved.Document.RequestDigest != binding.ParentRequestDigest || saved.Document.Request.TaskId?.ToString("D") != plan.SubjectId ||
            field.OperationId.ToString("D") != plan.OperationId || field.Disposition != SecretDisposition.Set ||
            field.Before.Revision != plan.ExpectedRevision || field.Before.SecretId?.ToString("D") != plan.BeforeSecretId ||
            field.After.SecretId?.ToString("D") != plan.Candidate.SecretId || field.JournalId?.ToString("D") != plan.JournalId ||
            field.Preparation != reference || field.ReplayProof != plan.ReplayProof ||
            !SameRetirement(field.Retirement, plan.Retirement))
            throw new IOException("secret_notification_parent_plan_unbound");
        return saved;
    }

    private static bool SameRetirement(NotificationSecretRetirement? saved, SecretPreparationDefsRetirement child)
    {
        if (saved is null) return child.State == "none" && child.JournalId is null && child.Plan is null;
        return saved.State == child.State && saved.JournalId?.ToString("D") == child.JournalId &&
            (saved.Plan is null ? child.Plan is null : child.Plan is { } reference &&
                saved.Plan == new BlobReference(reference.RelativePath, reference.SizeBytes, reference.Sha256));
    }
}
