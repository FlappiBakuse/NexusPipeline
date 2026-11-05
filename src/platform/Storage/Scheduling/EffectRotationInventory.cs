using System.Runtime.Versioning;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal static class EffectRotationInventory
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        IReadOnlyDictionary<Guid, WorkEffectOrigin> origins)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT claim_id,operation_id,source_operation_id,run_id,attempt_id,before_secret_id,before_revision,after_secret_id,after_revision FROM work_effect_rotations;";
        using var rows = command.ExecuteReader(); var count = 0;
        while (rows.Read())
        {
            if (++count > 100000) throw WorkEffectRows.Invalid();
            var claim = WorkEffectRows.Require(connection, transaction, Guid.ParseExact(rows.GetString(0), "D"));
            var operation = Guid.ParseExact(rows.GetString(1), "D");
            var origin = WorkEffectInventory.Require(origins, Guid.ParseExact(rows.GetString(2), "D"),
                Guid.ParseExact(rows.GetString(3), "D"), Guid.ParseExact(rows.GetString(4), "D"), claim.Key);
            var reference = SecretDocumentRows.Find(connection, transaction, operation) ?? throw WorkEffectRows.Invalid();
            var source = new SecretDocumentFormat(instance, contracts).Read(reference);
            if (source.Document.ExtensionId != claim.Key.ExtensionId || source.Document.SubjectId != "extension:" + claim.Key.ExtensionId ||
                SecretDocumentRows.Status(connection, transaction, source) != "completed") throw WorkEffectRows.Invalid();
            var change = source.Document.Secrets.SingleOrDefault(value => value.SubjectId == origin.TaskId && value.Field == claim.Key.Field);
            if (change is null || change.Disposition != SecretDisposition.Set || change.Before.SecretId?.ToString("D") != rows.GetString(5) ||
                change.Before.Revision != rows.GetInt64(6) || change.After.SecretId?.ToString("D") != rows.GetString(7) ||
                change.After.Revision != rows.GetInt64(8)) throw WorkEffectRows.Invalid();
        }
    }
}
