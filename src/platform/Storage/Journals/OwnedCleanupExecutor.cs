using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class OwnedCleanupExecutor(RuntimeInstance instance, ContractReader contracts, StateDatabase database, RecoveryIndex index)
{
    public FileCleanupResult Execute(Guid journalId, InstanceLease instanceLease, ResourceOperationLease resources)
        => ExecuteCore(journalId, instanceLease, resources, null, null, null);

    internal FileCleanupResult ExecutePrepared(Guid journalId, InstanceLease instanceLease, ResourceOperationLease resources,
        PayloadApplyRoots roots, ExtensionWorkExecution work, ICleanupPersistenceObserver? observer) =>
        ExecuteCore(journalId, instanceLease, resources, roots, work, observer);

    private FileCleanupResult ExecuteCore(Guid journalId, InstanceLease instanceLease, ResourceOperationLease resources,
        PayloadApplyRoots? roots, ExtensionWorkExecution? work, ICleanupPersistenceObserver? observer)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("cleanup_executor_instance_unbound");
        var registry = new RecoveryJournalRegistry(database);
        var registered = registry.ReadJournal(journalId);
        var plan = CleanupJournalBinding.ReadPlan(instance, contracts, registered, database, roots);
        if (plan.ExtensionWork is not null && work is null) throw new IOException("extension_work_executor_authority_required");
        var writers = new JournalWriterStore(database);
        CleanupWriterObservation Observe()
        {
            instanceLease.VerifyHeld(instance);
            if (!resources.IsActive || resources.OperationId != registered.OperationId ||
                !FileCleanupItems.Claims(plan).All(expected => resources.Claims.Contains(expected)))
                throw new IOException("cleanup_executor_resource_lease_required");
            // Only this proven, worker-free executor is excluded; every other writer must have stopped.
            work?.Require(plan);
            var current = writers.RequireCurrentExecutor(journalId);
            var observation = writers.Observe(journalId, current);
            return new(instance.InstanceId, journalId, observation.IsComplete, observation.State);
        }
        _ = Observe();
        var session = new CleanupRecoverySession(instance, journalId, contracts, database, Observe, observer, roots, work);
        index.BlockUnknown("cleanup_execution_active");
        try
        {
            using var evidence = registered.Phase == JournalPhase.Prepared ? work?.ProtectEvidence(plan) : null;
            using var history = session.ProtectReadHistory();
            var recovery = new FileCleanupRecovery(session);
            return registered.Phase == JournalPhase.Prepared ? recovery.Execute() : recovery.Recover();
        }
        finally
        {
            try
            {
                IRecoveryEvidence evidence = work is null ? new JournalRecoveryEvidence(instance, new(instance, contracts), database) :
                    OperatingSystem.IsWindows() ? new NexusPipeline.Platform.Storage.Execution.AdmissionPublicationEvidence(
                        instance, database, contracts, roots ?? throw new IOException("extension_work_roots_required")) :
                    throw new PlatformNotSupportedException();
                new RecoveryCoordinator(registry, evidence, index).VerifyAndPublish();
                if (work is not null)
                    NexusPipeline.Platform.Storage.Configuration.ConfigurationRecoveryHolds.Publish(database, index, roots);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { index.BlockUnknown("cleanup_execution_reconciliation_required"); }
        }
    }
}

