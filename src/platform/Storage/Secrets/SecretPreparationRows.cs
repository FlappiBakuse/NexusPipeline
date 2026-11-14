using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

internal enum SecretPreparationState { Pending, Published, Cancelled, Quarantined }

[SupportedOSPlatform("windows")]
internal static class SecretPreparationRows
{
    internal static SecretPreparationState Read(SqliteConnection connection, SqliteTransaction? transaction, Guid operationId, string? proof = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state,replay_proof FROM system.secret_preparations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !Enum.IsDefined((SecretPreparationState)reader.GetInt32(0))) throw new IOException("secret_preparation_state_missing");
        if (proof is not null && !SecretReplayProof.Matches(reader.GetString(1), proof)) throw new NexusPipeline.Core.Operations.OperationConflictException();
        return (SecretPreparationState)reader.GetInt32(0);
    }

    internal static void VerifyBefore(SqliteConnection connection, SqliteTransaction transaction, SecretPreparationPlan plan)
    {
        var before = SecretFieldRows.ReadField(connection, transaction, Key(plan));
        if (before.Revision != plan.ExpectedRevision || before.SecretId?.ToString("D") != plan.BeforeSecretId)
            throw new InvalidOperationException("secret_revision_conflict");
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, SecretPreparationPlan plan)
    {
        var c = plan.Candidate;
        Execute(connection, transaction, "INSERT INTO system.secret_payloads VALUES($0,$1,$2,$3,$4,$5,'prepared');",
            c.SecretId, plan.OperationId, c.ProtectionScheme, c.OwnerSid, c.Sha256, c.SizeBytes);
        Execute(connection, transaction, "INSERT INTO system.secret_preparations VALUES($0,$1,$2,0,$3);",
            plan.OperationId, plan.JournalId, c.SecretId, plan.ReplayProof);
    }

    internal static void SetState(SqliteConnection connection, SqliteTransaction transaction, SecretPreparationPlan plan, SecretPreparationState state)
    {
        Execute(connection, transaction, "UPDATE system.secret_preparations SET state=$0 WHERE operation_id=$1;", (int)state, plan.OperationId);
        if (state is SecretPreparationState.Published or SecretPreparationState.Cancelled)
            Execute(connection, transaction, "UPDATE main.operations SET status='completed' WHERE operation_id=$0;", plan.OperationId);
    }

    internal static SecretFieldKey Key(SecretPreparationPlan plan) => new(plan.OwnerKind switch
    {
        "extension" => FieldOwnerKind.Extension,
        "host" => FieldOwnerKind.Host,
        "notificationTask" => FieldOwnerKind.NotificationTask,
        _ => throw new IOException("secret_field_owner_invalid")
    }, plan.ExtensionId, Guid.Parse(plan.SubjectId), plan.Field,
        plan.OwnerKind == "notificationTask" ? Guid.Parse(plan.InstanceId) : null);

    internal static void VerifyPlan(SqliteConnection connection, SqliteTransaction transaction, SecretPreparationPlan plan)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT s.journal_id,s.candidate_id,s.replay_proof,p.protection_scheme,p.owner_sid,p.digest,p.size_bytes,p.preparation_operation_id,s.state,p.state " +
            "FROM system.secret_preparations s JOIN system.secret_payloads p ON p.secret_id=s.candidate_id WHERE s.operation_id=$id;";
        command.Parameters.AddWithValue("$id", plan.OperationId);
        using var row = command.ExecuteReader(); var c = plan.Candidate;
        if (!row.Read() || row.GetString(0) != plan.JournalId || row.GetString(1) != c.SecretId || row.GetString(2) != plan.ReplayProof ||
            row.GetString(3) != c.ProtectionScheme || row.GetString(4) != c.OwnerSid || row.GetString(5) != c.Sha256 ||
            row.GetInt64(6) != c.SizeBytes || row.GetString(7) != plan.OperationId ||
            ((SecretPreparationState)row.GetInt32(8) == SecretPreparationState.Published
                ? row.GetString(9) is not ("committed" or "retired") : row.GetString(9) != "prepared"))
            throw new IOException("secret_preparation_sql_plan_unbound");
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params object[] values)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$" + i, values[i]);
        command.ExecuteNonQuery();
    }
}
