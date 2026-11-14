using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal static class EffectEpochInventory
{
    internal static bool Verify(SqliteConnection connection, SqliteTransaction transaction, WorkEffectClaim claim,
        IReadOnlyDictionary<Guid, WorkEffectOrigin> origins)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT e.ordinal,e.operation_id,e.provider_id,e.task_id,e.run_id,e.attempt_id,e.secret_id,e.secret_revision,r.operation_id,r.run_id,r.attempt_id " +
            "FROM main.work_effect_epochs e LEFT JOIN main.work_effect_epoch_results r ON r.claim_id=e.claim_id AND r.ordinal=e.ordinal WHERE e.claim_id=$id ORDER BY e.ordinal;";
        command.Parameters.AddWithValue("$id", claim.Id.ToString("D"));
        using var rows = command.ExecuteReader(); long ordinal = 0; var closed = true;
        while (rows.Read())
        {
            if (claim.ParentId is not null || !closed || rows.GetInt64(0) != ++ordinal || ordinal > 4096 || rows.GetInt64(7) < 1)
                throw WorkEffectRows.Invalid();
            var owner = new WorkEffectOwner(Id(rows.GetString(1)), rows.GetString(2), Id(rows.GetString(3)), Id(rows.GetString(4)),
                Id(rows.GetString(5)), Id(rows.GetString(6)), rows.GetInt64(7));
            var source = WorkEffectInventory.Require(origins, owner.OperationId, owner.RunId, owner.AttemptId, claim.Key);
            if (owner.ProviderId != source.ProviderId || owner.TaskId != source.TaskId || ordinal == 1 && owner != claim.Owner)
                throw WorkEffectRows.Invalid();
            closed = !rows.IsDBNull(8);
            if (closed)
                _ = WorkEffectInventory.Require(origins, Id(rows.GetString(8)), Id(rows.GetString(9)), Id(rows.GetString(10)), claim.Key);
        }
        if (claim.ParentId is null && ordinal == 0) throw WorkEffectRows.Invalid();
        if (claim.ParentId is not null) return false;
        if (closed && WorkEffectRows.State(connection, transaction, claim.Id).Kind != "confirmed") throw WorkEffectRows.Invalid();
        return closed;
    }

    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw WorkEffectRows.Invalid();
}
