using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class OwnedCleanupExecutor(RuntimeInstance instance, ContractReader contracts, StateDatabase database, RecoveryIndex index)
{
    public FileCleanupResult Execute(Guid journalId, InstanceLease instanceLease, ResourceOperationLease resources,
        PayloadApplyRoots? roots = null)
        => ExecuteCore(journalId, instanceLease, resources, roots, null, null, null);

    internal FileCleanupResult ExecutePrepared(Guid journalId, InstanceLease instanceLease, ResourceOperationLease resources,
        PayloadApplyRoots roots, ExtensionWorkExecution work, ICleanupPersistenceObserver? observer) =>
        ExecuteCore(journalId, instanceLease, resources, roots, work, null, observer);

    internal FileCleanupResult ExecuteArchive(Guid journalId, InstanceLease instanceLease, ResourceOperationLease resources,
        PayloadApplyRoots roots, PhysicalObjectDeletion.FileLease held, BlobCleanup cleanup,
        ICleanupPersistenceObserver? observer, ProcessIdentityLease? executor = null) =>
        ExecuteCore(journalId, instanceLease, resources, roots, null, held, observer, executor, cleanup);

    private FileCleanupResult ExecuteCore(Guid journalId, InstanceLease instanceLease, ResourceOperationLease resources,
        PayloadApplyRoots? roots, ExtensionWorkExecution? work, PhysicalObjectDeletion.FileLease? held, ICleanupPersistenceObserver? observer,
        ProcessIdentityLease? executor = null, BlobCleanup? cleanup = null)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("cleanup_executor_instance_unbound");
        var registry = new RecoveryJournalRegistry(database);
        var registered = registry.ReadJournal(journalId);
        var plan = CleanupJournalBinding.ReadPlan(instance, contracts, registered, database, roots);
        if (plan.ExtensionWork is not null && work is null) throw new IOException("extension_work_executor_authority_required");
        using var ownedExecutor = executor is null && held is not null && OperatingSystem.IsWindows() ? ProcessIdentityLease.HoldCurrent() : null;
        var writers = new JournalWriterStore(database);
        CleanupWriterObservation Observe()
        {
            instanceLease.VerifyHeld(instance);
            if (!resources.IsActive || resources.OperationId != registered.OperationId ||
                !FileCleanupItems.Claims(plan).All(expected => resources.Claims.Contains(expected)))
                throw new IOException("cleanup_executor_resource_lease_required");
            // Only this proven, worker-free executor is excluded; every other writer must have stopped.
            work?.Require(plan);
            var current = writers.RequireCurrentExecutor(journalId, work?.Executor ?? executor ?? ownedExecutor);
            var observation = writers.Observe(journalId, current);
            return new(instance.InstanceId, journalId, observation.IsComplete, observation.State);
        }
        var initialObservation = Observe();
        var scopedAdmission = OperatingSystem.IsWindows() && roots is not null && work is null && held is null &&
            index.Current.IsReady && initialObservation is { IsComplete: true, State: WriterState.Stopped } &&
            plan.Items.All(item => item.Blob.RelativePath == $"blobs/content/{item.Blob.Sha256}.bin");
        if (!scopedAdmission) index.BlockUnknown("cleanup_execution_active");
        try
        {
            var blobs = new ImmutableBlobStore(instance, contracts);
            cleanup ??= new(instance, blobs, new(instance, contracts, database, blobs, roots));
            using var candidates = held is null && work is null ? cleanup.ProtectCandidates(plan.Items) : null;
            if (scopedAdmission)
            {
                try
                {
                    var published = cleanup.HasProtectedCandidates(plan.Items) && instanceLease.PublishExecution(() =>
                    {
                        if (!index.Current.IsReady || Observe() is not { IsComplete: true, State: WriterState.Stopped }) return false;
                        var admissionEvidence = new NexusPipeline.Platform.Storage.Execution.AdmissionPublicationEvidence(
                            instance, database, contracts, roots!, journalId);
                        new RecoveryCoordinator(registry, admissionEvidence, index).VerifyAndPublish();
                        NexusPipeline.Platform.Storage.Configuration.ConfigurationRecoveryHolds.Publish(database, index, roots!);
                        return true;
                    });
                    if (!published) index.BlockUnknown("cleanup_execution_active");
                }
                catch
                {
                    index.BlockUnknown("cleanup_execution_reconciliation_required");
                    throw;
                }
            }
            var session = new CleanupRecoverySession(instance, journalId, contracts, database, Observe, observer, roots, work, held, cleanup, instanceLease);
            using var evidence = registered.Phase == JournalPhase.Prepared ? work?.ProtectEvidence(plan) : null;
            using var history = session.ProtectReadHistory();
            var recovery = new FileCleanupRecovery(session);
            return registered.Phase == JournalPhase.Prepared ? recovery.Execute() : recovery.Recover();
        }
        finally
        {
            try
            {
                instanceLease.PublishExecution(() =>
                {
                    IRecoveryEvidence evidence = roots is null ? new JournalRecoveryEvidence(instance, new(instance, contracts), database) :
                        OperatingSystem.IsWindows() ? new NexusPipeline.Platform.Storage.Execution.AdmissionPublicationEvidence(
                            instance, database, contracts, roots, journalId) :
                        throw new PlatformNotSupportedException();
                    new RecoveryCoordinator(registry, evidence, index).VerifyAndPublish();
                    if (roots is not null)
                        NexusPipeline.Platform.Storage.Configuration.ConfigurationRecoveryHolds.Publish(database, index, roots);
                });
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                var reason = error.Message.Length is > 0 and <= 160 && error.Message.All(value =>
                    value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') ? error.Message : error.GetType().Name;
                Console.Error.WriteLine($"cleanup_execution_reconciliation_required {reason}");
                index.BlockUnknown("cleanup_execution_reconciliation_required");
            }
        }
    }
}

