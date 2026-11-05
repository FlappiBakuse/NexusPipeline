using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

internal sealed class InstallationPorts : IAsyncDisposable
{
    private readonly PersistentScheduleService _scheduling;
    private readonly SecretFieldService _secrets;
    private readonly SecretAccessPolicy _authorization;
    private readonly ExtensionStoreNetwork _network;

    internal InstallationPorts(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, HostLifecycle lifecycle, RecoveryIndex recovery, ContractReader contracts)
    {
        var execution = new ExecutionAdmissionService(instance, database, contracts, lease, resources, recovery, new(), new(5, 6, 7));
        _scheduling = new(instance, database, lease, resources, lifecycle, recovery, execution);
        _secrets = new(instance, database, lease, resources, recovery, contracts);
        _authorization = new(instance, database, lease, resources);
        _network = new(instance, database, lease, resources, () => new HostSettingsStore(database).Read().Values.Proxy, _secrets);
    }

    internal InstalledPortDependencies Dependencies(ExecutionLiveMedia? media) =>
        new(_scheduling, _secrets, _authorization, media?.Previews, media, media, _network.CreateClient);

    public ValueTask DisposeAsync() => new(_scheduling.StopAsync());
}
