using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record MaintenanceOperationRow(OperationIdentity Identity, BlobReference Plan, BlobReference Source,
    HostUpdateState State, Guid? HandoffId, string? TerminalCode, Guid? InitialJournalId);

internal static class MaintenanceOperationRows
{
    internal static MaintenanceOperationRow? ReadOwned(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, string subject)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT o.subject_id,o.request_digest,o.operation_kind,o.status,o.initial_journal_id," +
            "m.plan_sha256,m.plan_size,m.source_sha256,m.source_size,m.state,m.handoff_id,m.terminal_code " +
            "FROM maintenance_operations m JOIN operations o ON o.operation_id=m.operation_id " +
            "WHERE m.operation_id=$operation AND o.subject_id=$subject;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D")); command.Parameters.AddWithValue("$subject", subject);
        using var row = command.ExecuteReader();
        return row.Read() ? Decode(row, operationId) : null;
    }

    internal static void InsertPending(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity identity,
        BlobReference plan, BlobReference source)
    {
        RequirePlan(plan); RequirePlan(source);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO maintenance_operations(operation_id,plan_sha256,plan_size,source_sha256,source_size,state) " +
            "VALUES($operation,$plan,$planSize,$source,$sourceSize,0);";
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$plan", plan.Sha256); command.Parameters.AddWithValue("$planSize", plan.SizeBytes);
        command.Parameters.AddWithValue("$source", source.Sha256); command.Parameters.AddWithValue("$sourceSize", source.SizeBytes);
        command.ExecuteNonQuery();
    }

    internal static void CancelPending(SqliteConnection connection, SqliteTransaction transaction, MaintenanceOperationRow expected)
    {
        if (expected.State != HostUpdateState.Pending || expected.HandoffId is not null || expected.InitialJournalId is not null) throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE maintenance_operations SET state=3,terminal_code='cancelled-before-handoff' " +
            "WHERE operation_id=$operation AND state=0 AND handoff_id IS NULL AND terminal_code IS NULL " +
            "AND NOT EXISTS(SELECT 1 FROM operation_journals WHERE operation_id=$operation) " +
            "AND EXISTS(SELECT 1 FROM operations WHERE operation_id=$operation AND initial_journal_id IS NULL AND status='accepted');";
        command.Parameters.AddWithValue("$operation", expected.Identity.OperationId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw Invalid();
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation AND status='accepted' AND initial_journal_id IS NULL;";
        if (command.ExecuteNonQuery() != 1) throw Invalid();
    }

    internal static void MarkStarting(SqliteConnection connection, SqliteTransaction transaction, MaintenanceOperationRow expected, Guid handoffId)
    {
        if (expected.State != HostUpdateState.Pending || expected.HandoffId is not null || expected.InitialJournalId is not null || handoffId == Guid.Empty) throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE maintenance_operations SET state=1,handoff_id=$handoff WHERE operation_id=$operation AND state=0 " +
            "AND handoff_id IS NULL AND terminal_code IS NULL AND NOT EXISTS(SELECT 1 FROM operation_journals WHERE operation_id=$operation) " +
            "AND EXISTS(SELECT 1 FROM operations WHERE operation_id=$operation AND status='accepted' AND initial_journal_id IS NULL);";
        command.Parameters.AddWithValue("$operation", expected.Identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$handoff", handoffId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw Invalid();
    }

    internal static IEnumerable<MaintenanceOperationRow> ReadAll(SqliteConnection connection, SqliteTransaction transaction)
    {
        var after = "";
        while (true)
        {
            var identities = new List<(Guid Id, string Subject)>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT m.operation_id,o.subject_id FROM maintenance_operations m LEFT JOIN operations o ON o.operation_id=m.operation_id " +
                "WHERE m.operation_id>$after ORDER BY m.operation_id LIMIT 128;";
            command.Parameters.AddWithValue("$after", after);
            using (var rows = command.ExecuteReader()) while (rows.Read())
                identities.Add((Guid.Parse(rows.GetString(0)), rows.GetString(1)));
            foreach (var identity in identities) yield return ReadOwned(connection, transaction, identity.Id, identity.Subject) ?? throw Invalid();
            if (identities.Count < 128) yield break;
            after = identities[^1].Id.ToString("D");
        }
    }

    private static MaintenanceOperationRow Decode(SqliteDataReader row, Guid operationId)
    {
        var identity = new OperationIdentity(operationId, row.GetString(0), row.GetString(1), row.GetString(2));
        var state = (HostUpdateState)row.GetInt32(9);
        var initial = row.IsDBNull(4) ? (Guid?)null : Guid.Parse(row.GetString(4));
        var handoff = row.IsDBNull(10) ? (Guid?)null : Guid.Parse(row.GetString(10));
        var terminal = row.IsDBNull(11) ? null : row.GetString(11);
        if (operationId == Guid.Empty || identity.Kind != "maintenance.update" || identity.SubjectId is not { Length: >= 1 and <= 256 } || identity.SubjectId.Any(char.IsControl) ||
            !Digest(identity.RequestDigest) || !Enum.IsDefined(state) || initial == Guid.Empty || handoff == Guid.Empty ||
            (initial is null) != (handoff is null) ||
            (state is HostUpdateState.Pending or HostUpdateState.Cancelled) != (initial is null && handoff is null) ||
            (state is HostUpdateState.Completed or HostUpdateState.Cancelled or HostUpdateState.Quarantined) != (terminal is not null) ||
            terminal is not null && (terminal.Length is < 1 or > 80 || terminal.Any(char.IsControl)) ||
            state == HostUpdateState.Cancelled && terminal != "cancelled-before-handoff" ||
            row.GetString(3) != (state is HostUpdateState.Completed or HostUpdateState.Cancelled ? "completed" : "accepted")) throw Invalid();
        return new(identity, Plan(row.GetString(5), row.GetInt64(6)), Plan(row.GetString(7), row.GetInt64(8)), state, handoff, terminal, initial);
    }

    private static BlobReference Plan(string sha, long size)
    {
        var reference = new BlobReference($"blobs/plans/{sha}.json", size, sha); RequirePlan(reference); return reference;
    }
    private static void RequirePlan(BlobReference reference)
    {
        if (!Digest(reference.Sha256) || reference.SizeBytes is < 1 or > 2097152 || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json") throw Invalid();
    }
    private static bool Digest(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static IOException Invalid() => new("maintenance_operation_unbound");
}
