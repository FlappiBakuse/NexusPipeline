using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal static class MaintenanceOperationOutcome
{
    internal static void ProjectNoEffectInside(SqliteConnection connection, SqliteTransaction transaction, FrozenPayloadApply plan,
        string planDigest, JournalPhase phase, string detail)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id,operation_kind FROM main.operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", plan.OperationId.ToString("D"));
        string subject;
        using (var operation = command.ExecuteReader())
        {
            if (!operation.Read()) throw Invalid();
            if (operation.GetString(1) != "maintenance.update") return;
            subject = operation.GetString(0);
        }
        var row = MaintenanceOperationRows.ReadOwned(connection, transaction, plan.OperationId, subject) ?? throw Invalid();
        if (plan.Kind != JournalKind.UpdateApply || row.State != HostUpdateState.Starting || row.Plan.Sha256 != planDigest ||
            plan.MaintenanceSource != row.Source || row.InitialJournalId != plan.JournalId ||
            phase is not (JournalPhase.Committed or JournalPhase.Quarantined) || detail is not ("payload_not_executed" or
                "payload_writer_stop_unconfirmed" or "payload_effect_entry_observed" or "payload_prepared_no_effect_unverified") ||
            (phase == JournalPhase.Committed) != (detail == "payload_not_executed")) throw Invalid();
        command.CommandText = "UPDATE system.maintenance_operations SET state=$state,terminal_code=$detail " +
            "WHERE operation_id=$operation AND state=1 AND handoff_id IS NOT NULL;";
        command.Parameters.AddWithValue("$state", phase == JournalPhase.Committed ? 2 : 4);
        command.Parameters.AddWithValue("$detail", detail);
        if (command.ExecuteNonQuery() != 1) throw Invalid();
        if (phase == JournalPhase.Committed)
        {
            command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation AND status='accepted' AND initial_journal_id=$journal;";
            command.Parameters.AddWithValue("$journal", plan.JournalId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw Invalid();
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void ProjectRestoredInside(SqliteConnection connection, SqliteTransaction transaction,
        BoundMaintenancePayload bound, JournalDocument document, JournalRecoveryOwnership.MaintenanceStopProof stop,
        RestoredPayloadLease restored)
    {
        stop.RequireInside(connection, transaction, bound.Plan.JournalId);
        restored.Require(bound.Plan, MaintenancePhysicalJournal.Scene(bound.Physical[^1].Record));
        MaintenanceRestorationCodec.RequireRevision(document, bound.Plan, bound.Restoration, bound.Physical.Length);
        var current = NexusPipeline.Platform.Storage.Sqlite.RecoveryJournalRegistry.ReadJournalInside(connection, transaction, bound.Plan.JournalId);
        var row = MaintenanceOperationRows.ReadOwned(connection, transaction, bound.Plan.OperationId,
            bound.Operation.Identity.SubjectId) ?? throw Invalid();
        if (current.Kind != JournalKind.UpdateApply || current.Phase != JournalPhase.Committed ||
            current.Revision != document.Revision || current.PlanPath != bound.PlanReference.RelativePath ||
            current.PlanDigest != bound.PlanReference.Sha256 || document.Phase != JournalPhase.Committed ||
            bound.Restoration.IsEmpty || bound.Restoration[^1].Record.Stage != "committed" ||
            row != bound.Operation || row.State != HostUpdateState.Starting || row.HandoffId is null ||
            row.InitialJournalId != bound.Plan.JournalId || row.Plan != bound.PlanReference || row.Source != bound.Plan.MaintenanceSource)
            throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE system.maintenance_operations SET state=2,terminal_code='maintenance_original_restored' " +
            "WHERE operation_id=$operation AND state=1 AND handoff_id=$handoff AND terminal_code IS NULL;";
        command.Parameters.AddWithValue("$operation", bound.Plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$handoff", row.HandoffId.Value.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw Invalid();
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation AND status='accepted' AND initial_journal_id=$journal;";
        command.Parameters.AddWithValue("$journal", bound.Plan.JournalId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw Invalid();
    }

    private static IOException Invalid() => new("maintenance_no_effect_outcome_unbound");
}
