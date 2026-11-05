using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationRevisionService
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _instanceLease;
    private readonly ResourceLeaseManager _resources;
    private readonly IConfigurationRevisionStore _store;

    public ConfigurationRevisionService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease instanceLease, ResourceLeaseManager resources)
    {
        instanceLease.VerifyHeld(instance);
        _instance = instance; _instanceLease = instanceLease; _resources = resources;
        _store = new ConfigurationRevisionStore(instance, database, contracts);
    }

    public ConfigurationRevision? Read(ConfigurationAddress address)
    {
        _instanceLease.VerifyHeld(_instance);
        return _store.Read(address);
    }

    public ConfigurationSaveResult Save(OperationIdentity operation, ConfigurationRevisionChange change, DateTimeOffset acceptedAt)
    {
        _instanceLease.VerifyHeld(_instance);
        change = ConfigurationRevisionPolicy.Freeze(change);
        var replay = _store.Lookup(operation, change);
        if (replay is not null) return replay;
        var admission = _resources.TryAcquire(operation.OperationId, Claims(change.Address));
        using var lease = admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "configuration_resource_conflict" : "configuration_host_unavailable");
        return _store.Commit(operation, change, acceptedAt);
    }

    public static string ResourceKey(ConfigurationAddress address) => $"configuration:{address.BindingId:D}:{address.ResourceId}";
    internal static IReadOnlyList<ResourceClaim> Claims(ConfigurationAddress address) => [
        new("automation:definitions", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
        new(ResourceKey(address), ResourceClaimMode.Mutation)];
}
