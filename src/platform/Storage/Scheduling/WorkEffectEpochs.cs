using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed record WorkEffectCloser(Guid OperationId, Guid RunId, Guid AttemptId);
internal sealed record WorkEffectEpoch(Guid ClaimId, long Ordinal, WorkEffectOwner Owner, WorkEffectCloser? Closed);

internal static class WorkEffectEpochs
{
    internal static WorkEffectEpoch Latest(SqliteConnection connection, SqliteTransaction? transaction, Guid claimId)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT e.ordinal,e.operation_id,e.provider_id,e.task_id,e.run_id,e.attempt_id,e.secret_id,e.secret_revision,r.operation_id,r.run_id,r.attempt_id " +
            "FROM work_effect_epochs e LEFT JOIN work_effect_epoch_results r ON r.claim_id=e.claim_id AND r.ordinal=e.ordinal WHERE e.claim_id=$0 ORDER BY e.ordinal DESC LIMIT 1;", claimId.ToString("D"));
        using var row = command.ExecuteReader(); if (!row.Read()) throw WorkEffectRows.Invalid();
        var ordinal = row.GetInt64(0); if (ordinal is < 1 or > 4096 || row.GetInt64(7) < 1) throw WorkEffectRows.Invalid();
        return new(claimId, ordinal, new(Id(row.GetString(1)), row.GetString(2), Id(row.GetString(3)), Id(row.GetString(4)),
            Id(row.GetString(5)), Id(row.GetString(6)), row.GetInt64(7)),
            row.IsDBNull(8) ? null : new(Id(row.GetString(8)), Id(row.GetString(9)), Id(row.GetString(10))));
    }

    internal static WorkEffectEpoch Open(SqliteConnection connection, SqliteTransaction transaction, Guid claimId, long ordinal, WorkEffectOwner owner)
    {
        if (ordinal is < 1 or > 4096) throw new IOException("work_effect_epoch_limit");
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "INSERT INTO work_effect_epochs VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8);", claimId.ToString("D"), ordinal, owner.OperationId.ToString("D"),
            owner.ProviderId, owner.TaskId.ToString("D"), owner.RunId.ToString("D"), owner.AttemptId.ToString("D"), owner.SecretId.ToString("D"), owner.SecretRevision);
        command.ExecuteNonQuery(); return new(claimId, ordinal, owner, null);
    }

    internal static WorkEffectEpoch Require(SqliteConnection connection, SqliteTransaction? transaction, Guid root, long ordinal)
    {
        var actual = Latest(connection, transaction, root);
        if (actual.Ordinal != ordinal) throw new UnauthorizedAccessException("work_effect_epoch_changed"); return actual;
    }

    internal static void Close(SqliteConnection connection, SqliteTransaction transaction, WorkEffectEpoch epoch, WorkEffectCloser closer)
    {
        if (epoch.Closed is not null) return;
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "INSERT INTO work_effect_epoch_results VALUES($0,$1,$2,$3,$4);", epoch.ClaimId.ToString("D"), epoch.Ordinal,
            closer.OperationId.ToString("D"), closer.RunId.ToString("D"), closer.AttemptId.ToString("D")); command.ExecuteNonQuery();
    }

    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw WorkEffectRows.Invalid();
}
