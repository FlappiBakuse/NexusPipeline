using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Host.Lifecycle;

internal sealed record ReadyRuntime(StateDatabase Database, RecoveryIndex Recovery);

internal static class RuntimeReadiness
{
    public static ReadyRuntime Verify(RuntimeInstance instance, ContractReader contracts, InstanceLease lease, PayloadApplyRoots roots)
    {
        using var paths = DirectoryAttributeScope.Hold(instance);
        using var blobs = BlobReadScope.Hold(instance);
        var trust = Platform.Storage.Maintenance.UpdateReleaseTrust.Configured();
        var database = File.Exists(Path.Combine(instance.RuntimeRoot, "state-initialization.guard"))
            ? StateDatabase.OpenExisting(instance, Path.Combine(instance.RuntimeRoot, "diagnostics", "state-inspection"),
                new JournalStartupAdmission(instance, contracts, roots, trust), lease)
            : StateDatabase.CreateFresh(instance);
        var index = new StartupJournalRecovery(instance, contracts, database, roots, trust).Recover(lease);
        Platform.Storage.Settings.StartupRegistrationCoordinator.ReconcileStopped(instance, database, lease, index,
            new Platform.Windows.StartupRegistrationEffects());
        return new(database, index);
    }
}
