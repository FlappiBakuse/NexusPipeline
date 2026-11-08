using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Automation;
using System.Runtime.ExceptionServices;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class JournalStartupAdmission(RuntimeInstance instance, ContractReader contracts, PayloadApplyRoots? payloadRoots = null,
    Maintenance.UpdateReleaseTrust? maintenanceTrust = null) : IDatabaseStartupAdmission
{
    public void Verify(DatabaseInspection inspection)
    {
        var database = StateDatabase.Inspected(inspection, instance);
        var snapshot = VerifyContents(inspection, database);
        new JournalRecoveryOwnership(database).VerifyRecoverable(snapshot.Journals);
        using (StartupProfile.Measure("inventory.ManualWorkInventory"))
            Scheduling.ManualWorkInventory.VerifyStopped(instance, database, contracts);
    }

    internal RecoveryRegistrySnapshot VerifyContents(DatabaseInspection inspection)
        => VerifyContents(inspection, StateDatabase.Inspected(inspection, instance));

    private RecoveryRegistrySnapshot VerifyContents(DatabaseInspection inspection, StateDatabase database)
    {
        using var timing = StartupProfile.Measure("database.contents");
        if (inspection.InstanceId != instance.InstanceId) throw new IOException("startup_database_instance_unbound");
        var roots = payloadRoots ?? new PayloadApplyRoots(PhysicalPathPolicy.RuntimeApplicationDirectory(), Path.Combine(instance.RuntimeRoot, "extensions"));
        var journals = new StartupJournalRecovery(instance, contracts, database, roots, maintenanceTrust);
        ExceptionDispatchInfo? inventoryFailure = null, historyFailure = null;
        // Both branches are read-only. Join them before active recovery or releasing the inspected copy.
        Parallel.Invoke(new ParallelOptions { MaxDegreeOfParallelism = 2 },
            () =>
            {
                try { VerifyInventories(database, roots); }
                catch (Exception error) { inventoryFailure = ExceptionDispatchInfo.Capture(error); }
            },
            () =>
            {
                try { journals.InspectCompletedHistory(); }
                catch (Exception error) { historyFailure = ExceptionDispatchInfo.Capture(error); }
            });
        inventoryFailure?.Throw();
        historyFailure?.Throw();
        var snapshot = RecoveryJournalRegistry.ReadInspectedSnapshot(inspection);
        journals.InspectBindings(snapshot);
        return snapshot;
    }

    private void VerifyInventories(StateDatabase database, PayloadApplyRoots roots)
    {
        using var reads = new HistoryReadScope(instance, database, contracts);
        using var parents = JournalReadScope.HoldParents(instance, contracts);
        using (StartupProfile.Measure("inventory.AutomationDefinitionStore"))
            _ = new AutomationDefinitionStore(database).Read();
        using (StartupProfile.Measure("inventory.HostSettingsStore"))
            _ = new HostSettingsStore(database).Read();
        using (StartupProfile.Measure("inventory.ConfigurationRevisionStore"))
            Configuration.ConfigurationRevisionStore.VerifyReadOnly(instance, database, contracts);
        using (StartupProfile.Measure("inventory.ConfigurationBatchInventory"))
            Configuration.ConfigurationBatchInventory.VerifyReadOnly(instance, database, contracts);
        using (StartupProfile.Measure("inventory.ConfigurationEditInventory"))
            Configuration.ConfigurationEditInventory.VerifyReadOnly(database);
        using (StartupProfile.Measure("inventory.ConfigurationRunInventory"))
            Configuration.ConfigurationRunInventory.VerifyReadOnly(instance, database, contracts);
        using (StartupProfile.Measure("inventory.ScheduleMinuteInventory"))
            Scheduling.ScheduleMinuteInventory.Verify(database);
        using (StartupProfile.Measure("inventory.StartupQueueInventory"))
            Scheduling.StartupQueueInventory.Verify(instance, database);
        using (StartupProfile.Measure("inventory.ScheduledProviderInventory"))
            Scheduling.ScheduledProviderInventory.Visit(instance, database, contracts, _ => { });
        using (StartupProfile.Measure("inventory.StartSourceRows"))
            Execution.StartSourceRows.VerifyInventory(instance, database, contracts);
        using (StartupProfile.Measure("inventory.RunBudgetInventory"))
            Execution.RunBudgetInventory.Verify(instance, database, contracts);
        using (StartupProfile.Measure("inventory.ExecutionRetryInventory"))
            Execution.ExecutionRetryInventory.Verify(instance, database, contracts);
        using (StartupProfile.Measure("inventory.CompletionActionStartup"))
            Execution.CompletionActionStartup.VerifyReadOnly(instance, database, contracts);
        using (StartupProfile.Measure("inventory.JournalResourceClaims"))
            JournalResourceClaims.VerifyInventory(instance, database, contracts, payloadRoots);
        using (StartupProfile.Measure("inventory.JobClosureStore"))
            JobClosureStore.VerifyInventory(instance, database, contracts);
        using (StartupProfile.Measure("inventory.RecoveryCapacityInventory"))
            RecoveryCapacityInventory.Verify(instance, database, contracts);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using (StartupProfile.Measure("inventory.ExecutionCancellationStore"))
            Execution.ExecutionCancellationStore.VerifyInventory(instance, database, contracts);
        using (StartupProfile.Measure("inventory.RecordRetirementInventory"))
            Records.RecordRetirementInventory.VerifyReadOnly(instance, database, contracts);
        using (StartupProfile.Measure("inventory.LogCatalogInventory"))
            Execution.LogCatalogInventory.VerifyReadOnly(instance, database, contracts);
        using (StartupProfile.Measure("inventory.ImageCatalogInventory"))
            Execution.ImageCatalogInventory.VerifyReadOnly(instance, database, contracts);
        using (StartupProfile.Measure("inventory.StartupRegistrationCoordinator"))
            Settings.StartupRegistrationCoordinator.VerifyReadOnly(database);
        using (StartupProfile.Measure("inventory.Notifications"))
            new Notifications.NotificationPublicationInventory(instance, database, contracts).VerifyReadOnly();
        using (StartupProfile.Measure("inventory.Secrets"))
            new Secrets.SecretDocumentInventory(instance, database, contracts).Verify();
        using (StartupProfile.Measure("inventory.ManualWorkInventory"))
            Scheduling.ManualWorkInventory.Visit(instance, database, contracts, _ => { });
        using (StartupProfile.Measure("inventory.WorkEffectInventory"))
            Scheduling.WorkEffectInventory.Verify(instance, database, contracts, rotations: true);
        using (StartupProfile.Measure("inventory.DeliveryReferenceIndex"))
            Notifications.DeliveryReferenceIndex.VerifyReadOnly(instance, database, contracts);
        using (StartupProfile.Measure("inventory.SecretFieldStore"))
            SecretFieldStore.VerifyInventory(database);
        using (StartupProfile.Measure("inventory.PreparationEvidenceStore"))
            Secrets.PreparationEvidenceStore.VerifyInventory(instance, database);
        using (StartupProfile.Measure("inventory.MaintenancePendingInventory"))
            Maintenance.MaintenancePendingInventory.VerifyReadOnly(instance, database, contracts, roots);
    }
}
