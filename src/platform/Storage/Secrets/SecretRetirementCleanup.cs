using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed class SecretRetirementCleanup : IDisposable
{
    private readonly List<(FrozenFileCleanup Plan, ResourceOperationLease Lease)> _items = [];

    internal SecretRetirementCleanup(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        ResourceLeaseManager resources, IEnumerable<BlobReference> references)
    {
        try
        {
            foreach (var reference in references.Distinct())
            {
                var plan = new CleanupPlanCodec(instance, contracts, new(instance, contracts)).Read(reference);
                if (plan.ExtensionWork is not null || plan.Items.Count != 1 ||
                    CleanupCandidateFiles.SecretId(plan.Items[0].Blob) is null)
                    throw new IOException("secret_cleanup_plan_unbound");
                var admission = resources.TryAcquire(plan.OperationId, FileCleanupItems.Claims(plan));
                var held = admission.Lease ?? throw new IOException("secret_cleanup_resource_unavailable");
                _items.Add((plan, held));
            }
        }
        catch { Dispose(); throw; }
    }

    internal void Finish(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, RecoveryIndex index)
    {
        foreach (var item in _items)
            new OwnedCleanupExecutor(instance, contracts, database, index).Execute(item.Plan.JournalId, lease, item.Lease);
    }

    public void Dispose()
    {
        for (var index = _items.Count - 1; index >= 0; index--) _items[index].Lease.Dispose();
        _items.Clear();
    }
}
