using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed record WorkEffectKey(string ExtensionId, string Field, string ActivityId, string LocalDate, string Family);
internal sealed record WorkEffectOwner(Guid OperationId, string ProviderId, Guid TaskId, Guid RunId, Guid AttemptId, Guid SecretId, long SecretRevision);
internal sealed record WorkEffectClaim(Guid Id, WorkEffectKey Key, Guid? ParentId, string RoleId, string ServerId, WorkEffectOwner Owner);
internal sealed record WorkEffectEvent(long Sequence, string Kind);

internal static class WorkEffectRows
{
    internal static void Pin(SqliteConnection connection, SqliteTransaction transaction, string extensionId, string field, string version)
    {
        using var insert = Command(connection, transaction,
            "INSERT INTO main.work_identity_versions VALUES($0,$1,$2) ON CONFLICT(extension_id,field) DO NOTHING;", extensionId, field, version);
        insert.ExecuteNonQuery();
        using var read = Command(connection, transaction,
            "SELECT identity_version FROM main.work_identity_versions WHERE extension_id=$0 AND field=$1;", extensionId, field);
        if (!Equals(read.ExecuteScalar(), version)) throw new IOException("work_identity_migration_required");
    }

    internal static WorkEffectClaim? Find(SqliteConnection connection, SqliteTransaction? transaction, WorkEffectKey key, string roleId, string serverId)
    {
        using var command = Command(connection, transaction,
            "SELECT * FROM main.work_effect_claims WHERE extension_id=$0 AND field=$1 AND activity_id=$2 AND local_date=$3 AND family_sha256=$4 AND role_id=$5 AND server_id=$6;",
            key.ExtensionId, key.Field, key.ActivityId, key.LocalDate, key.Family, roleId, serverId);
        using var row = command.ExecuteReader(); return row.Read() ? Read(row) : null;
    }

    internal static WorkEffectClaim Require(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Command(connection, transaction, "SELECT * FROM main.work_effect_claims WHERE claim_id=$0;", id.ToString("D"));
        using var row = command.ExecuteReader(); return row.Read() ? Read(row) : throw Invalid();
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, WorkEffectClaim claim)
    {
        var owner = claim.Owner; var key = claim.Key;
        using var command = Command(connection, transaction,
            "INSERT INTO main.work_effect_claims VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15);",
            claim.Id.ToString("D"), key.ExtensionId, key.Field, key.ActivityId, key.LocalDate, key.Family,
            claim.ParentId?.ToString("D"), claim.RoleId, claim.ServerId, owner.OperationId.ToString("D"), owner.ProviderId,
            owner.TaskId.ToString("D"), owner.RunId.ToString("D"), owner.AttemptId.ToString("D"), owner.SecretId.ToString("D"), owner.SecretRevision);
        command.ExecuteNonQuery(); Append(connection, transaction, claim.Id, owner, "acquired", null);
    }

    internal static WorkEffectEvent State(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Command(connection, transaction,
            "SELECT sequence,kind,evidence_code FROM main.work_effect_events WHERE claim_id=$0 ORDER BY sequence;", id.ToString("D"));
        using var rows = command.ExecuteReader(); long sequence = 0; string? kind = null;
        while (rows.Read())
        {
            if (rows.GetInt64(0) != ++sequence || sequence > 4096) throw Invalid();
            var next = rows.GetString(1);
            if (sequence == 1 ? next != "acquired" : kind == "confirmed" || next is not ("submitted" or "confirmed") || next == "submitted" && kind != "acquired")
                throw Invalid();
            if (next == "confirmed" ? rows.IsDBNull(2) : !rows.IsDBNull(2)) throw Invalid();
            kind = next;
        }
        return kind is null ? throw Invalid() : new(sequence, kind);
    }

    internal static void Append(SqliteConnection connection, SqliteTransaction transaction, Guid id, WorkEffectOwner owner, string kind, string? evidence)
    {
        using var command = Command(connection, transaction,
            "INSERT INTO main.work_effect_events SELECT $0,COALESCE(MAX(sequence),0)+1,$1,$2,$3,$4,$5 FROM main.work_effect_events WHERE claim_id=$0;",
            id.ToString("D"), owner.OperationId.ToString("D"), owner.RunId.ToString("D"), owner.AttemptId.ToString("D"), kind, evidence);
        command.ExecuteNonQuery();
    }

    internal static IReadOnlyList<Guid> Children(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Command(connection, transaction, "SELECT claim_id FROM main.work_effect_claims WHERE parent_id=$0 ORDER BY claim_id;", id.ToString("D"));
        using var rows = command.ExecuteReader(); var result = new List<Guid>();
        while (rows.Read()) { if (result.Count >= 1024) throw Invalid(); result.Add(Id(rows.GetString(0))); }
        return result;
    }

    private static WorkEffectClaim Read(SqliteDataReader row)
    {
        var key = new WorkEffectKey(row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5));
        if (!DateOnly.TryParseExact(key.LocalDate, "yyyy-MM-dd", out _) || key.Family.Length != 64 ||
            key.Family.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();
        return new(Id(row.GetString(0)), key, row.IsDBNull(6) ? null : Id(row.GetString(6)), row.GetString(7), row.GetString(8),
            new(Id(row.GetString(9)), row.GetString(10), Id(row.GetString(11)), Id(row.GetString(12)), Id(row.GetString(13)), Id(row.GetString(14)), row.GetInt64(15)));
    }

    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw Invalid();
    internal static IOException Invalid() => new("work_effect_facts_unverified");
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params object?[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$" + index, values[index] ?? DBNull.Value);
        return command;
    }
}
