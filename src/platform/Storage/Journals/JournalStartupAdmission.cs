using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Automation;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class JournalStartupAdmission(RuntimeInstance instance, ContractReader contracts, PayloadApplyRoots? payloadRoots = null,
    Maintenance.UpdateReleaseTrust? maintenanceTrust = null) : IDatabaseStartupAdmission
{
    public void Verify(DatabaseInspection inspection)
    {
        var database = StateDatabase.Inspected(inspection, instance);
        var snapshot = VerifyContents(inspection, database);
        new JournalRecoveryOwnership(database).VerifyRecoverable(snapshot.Journals);
        Scheduling.ManualWorkInventory.VerifyStopped(instance, database, contracts);
    }

    internal RecoveryRegistrySnapshot VerifyContents(DatabaseInspection inspection)
        => VerifyContents(inspection, StateDatabase.Inspected(inspection, instance));

    private RecoveryRegistrySnapshot VerifyContents(DatabaseInspection inspection, StateDatabase database)
    {
        if (inspection.InstanceId != instance.InstanceId) throw new IOException("startup_database_instance_unbound");
        using var parents = JournalReadScope.HoldParents(instance, contracts);
        _ = new AutomationDefinitionStore(database).Read();
        _ = new HostSettingsStore(database).Read();
        Configuration.ConfigurationRevisionStore.VerifyReadOnly(instance, database, contracts);
        Configuration.ConfigurationBatchInventory.VerifyReadOnly(instance, database, contracts);
        Configuration.ConfigurationEditInventory.VerifyReadOnly(database);
        Configuration.ConfigurationRunInventory.VerifyReadOnly(instance, database, contracts);
        Scheduling.ScheduleMinuteInventory.Verify(database);
        Scheduling.StartupQueueInventory.Verify(instance, database);
        Scheduling.ScheduledProviderInventory.Visit(instance, database, contracts, _ => { });
        Execution.StartSourceRows.VerifyInventory(instance, database, contracts);
        Execution.RunBudgetInventory.Verify(instance, database, contracts);
        Execution.ExecutionRetryInventory.Verify(instance, database, contracts);
        Execution.CompletionActionStartup.VerifyReadOnly(instance, database, contracts);
        JournalResourceClaims.VerifyInventory(instance, database, contracts, payloadRoots);
        JobClosureStore.VerifyInventory(instance, database, contracts);
        RecoveryCapacityInventory.Verify(instance, database, contracts);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Execution.ExecutionCancellationStore.VerifyInventory(instance, database, contracts);
        Records.RecordRetirementInventory.VerifyReadOnly(instance, database, contracts);
        Execution.LogCatalogInventory.VerifyReadOnly(instance, database, contracts);
        Execution.ImageCatalogInventory.VerifyReadOnly(instance, database, contracts);
        Settings.StartupRegistrationCoordinator.VerifyReadOnly(database);
        new Notifications.NotificationPublicationInventory(instance, database, contracts).VerifyReadOnly();
        new Secrets.SecretDocumentInventory(instance, database, contracts).Verify();
        Scheduling.ManualWorkInventory.Visit(instance, database, contracts, _ => { });
        Scheduling.WorkEffectInventory.Verify(instance, database, contracts,
                rotations: true);
        Notifications.DeliveryReferenceIndex.VerifyReadOnly(instance, database, contracts);
        SecretFieldStore.VerifyInventory(database);
        Secrets.PreparationEvidenceStore.VerifyInventory(instance, database);
        var snapshot = RecoveryJournalRegistry.ReadInspectedSnapshot(inspection);
        var roots = payloadRoots ?? new PayloadApplyRoots(PhysicalPathPolicy.RuntimeApplicationDirectory(), Path.Combine(instance.RuntimeRoot, "extensions"));
        Maintenance.MaintenancePendingInventory.VerifyReadOnly(instance, database, contracts, roots);
        var journals = new StartupJournalRecovery(instance, contracts, database, roots, maintenanceTrust);
        journals.InspectBindings(snapshot);
        journals.InspectCompletedHistory();
        return snapshot;
    }
}
