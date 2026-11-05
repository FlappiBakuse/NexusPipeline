using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class MaintenanceDatabaseAdmission(RuntimeInstance instance, ContractReader contracts, PayloadApplyRoots roots,
    UpdateReleaseTrust trust, MaintenanceStartupBinding binding, JournalPhase expectedPhase, Action requireCurrentPeer,
    Action<SqliteConnection, SqliteTransaction> requireInspectedBinding)
    : IDatabaseStartupAdmission
{
    public void Verify(DatabaseInspection inspection)
    {
        requireCurrentPeer(); MaintenanceStartupFrames.Require(binding);
        if (binding.InstanceId != instance.InstanceId || expectedPhase is not (JournalPhase.Prepared or JournalPhase.Applied or JournalPhase.Committed))
            throw Invalid();
        var snapshot = new JournalStartupAdmission(instance, contracts, roots, trust).VerifyContents(inspection);
        var own = snapshot.Journals.SingleOrDefault(item => item.JournalId == binding.JournalId) ?? throw Invalid();
        if (own.Kind != JournalKind.UpdateApply || own.Phase != expectedPhase || own.OperationId != binding.OperationId ||
            own.PlanPath != binding.Plan.RelativePath || own.PlanDigest != binding.Plan.Sha256 ||
            snapshot.Journals.Any(item => item.JournalId != own.JournalId && item.Phase != JournalPhase.Committed) ||
            snapshot.Isolations.Any(item => item.JournalId != own.JournalId)) throw Invalid();
        // A SQLite reader can change SHM; all SQL checks must use the frozen copy during admission.
        using var connection = DatabaseConnections.Open(Path.Combine(inspection.DiagnosticCopyRoot, "nexus.db"), readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        requireInspectedBinding(connection, transaction);
        transaction.Commit();
        requireCurrentPeer();
    }
    private static IOException Invalid() => new("maintenance_database_admission_unbound");
}
