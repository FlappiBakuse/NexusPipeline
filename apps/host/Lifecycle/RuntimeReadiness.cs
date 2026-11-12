using NexusPipeline.Core.Configuration;
using NexusPipeline.Host.Desktop;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Host.Lifecycle;

internal sealed record ReadyRuntime(StateDatabase Database, RecoveryIndex Recovery,
    Platform.Storage.Notifications.NotificationStartupState? Notifications);

internal static class RuntimeReadiness
{
    public static ReadyRuntime Verify(RuntimeInstance instance, ContractReader contracts, InstanceLease lease, PayloadApplyRoots roots,
        Action<StartupStage>? reportProgress = null)
    {
        using var paths = DirectoryAttributeScope.Hold(instance);
        using var blobs = BlobReadScope.Hold(instance);
        using var identity = InstanceReadScope.Hold(instance, contracts);
        using var executor = new Platform.Processes.ProcessIdentityScope(lease.ExecutorIdentity);
        var trust = Platform.Storage.Maintenance.UpdateReleaseTrust.Configured();
        reportProgress?.Invoke(StartupStage.CheckingDatabase);
        var database = File.Exists(Path.Combine(instance.RuntimeRoot, "state-initialization.guard"))
            ? StateDatabase.OpenCurrent(instance, lease)
            : StateDatabase.CreateFresh(instance, lease);
        reportProgress?.Invoke(StartupStage.RecoveringOperations);
        var index = new StartupJournalRecovery(instance, contracts, database, roots, trust).Recover(lease);
        Platform.Storage.Settings.StartupRegistrationCoordinator.ReconcileStopped(instance, database, lease, index,
            new Platform.Windows.StartupRegistrationEffects());
        return new(database, index, null);
    }
}
