using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.WindowsTests;

internal sealed record SecretTestRuntime(RuntimeInstance Instance, StateDatabase Database, InstanceLease Lease, RecoveryIndex Index,
    HostLifecycle Lifecycle, ResourceLeaseManager Resources, SecretFieldStore Store, SecretFieldService Service, SecretFieldKey Key) : IDisposable
{
    internal static SecretTestRuntime Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "secret-runtime-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var lease = InstanceLease.Acquire(instance, new());
        try
        {
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
            var lifecycle = new HostLifecycle(index, TimeProvider.System);
            var resources = new ResourceLeaseManager(lifecycle);
            return new(instance, database, lease, index, lifecycle, resources, new(instance, database),
                new(instance, database, lease, resources, index, new()), new("fixture-extension", Guid.NewGuid(), "credential"));
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose() => Lease.Dispose();
}
