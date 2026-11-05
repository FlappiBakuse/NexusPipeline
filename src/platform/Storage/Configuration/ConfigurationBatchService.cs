using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationBatchService
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _instanceLease;
    private readonly ResourceLeaseManager _resources;
    private readonly ConfigurationBatchStore _store;

    public ConfigurationBatchService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease instanceLease, ResourceLeaseManager resources, IBatchPersistenceObserver? observer = null)
    {
        instanceLease.VerifyHeld(instance);
        _instance = instance; _instanceLease = instanceLease; _resources = resources;
        _store = new(instance, database, contracts, observer);
    }

    public ConfigurationBatchResult Save(OperationIdentity operation, ConfigurationRevisionBatch batch, DateTimeOffset acceptedAt)
    {
        _instanceLease.VerifyHeld(_instance);
        batch = RevisionBatchPolicy.Freeze(batch);
        var replay = _store.Lookup(operation, batch);
        if (replay is not null) return replay;
        var claims = batch.Changes.SelectMany(change => ConfigurationRevisionService.Claims(change.Address)).Distinct().ToArray();
        var admission = _resources.TryAcquire(operation.OperationId, claims);
        using var lease = admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "configuration_resource_conflict" : "configuration_host_unavailable");
        using var prepared = _store.Prepare(batch);
        return _store.Commit(operation, prepared, acceptedAt);
    }
}
