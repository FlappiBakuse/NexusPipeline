using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class PreparedWorkCleanup : IDisposable
{
    private readonly InstanceLease _lease;
    private readonly ResourceOperationBorrow _borrow;
    private readonly WorkScopeProtection _inventory;
    private readonly FrozenFileCleanup _plan;
    private int _disposed;
    internal ExtensionWorkExecution Execution { get; }
    internal OperationIdentity Identity { get; }

    private PreparedWorkCleanup(InstanceLease lease, ResourceOperationBorrow borrow,
        WorkScopeProtection inventory, FrozenFileCleanup plan,
        ExtensionWorkExecution execution, OperationIdentity identity)
    { _lease = lease; _borrow = borrow; _inventory = inventory; _plan = plan; Execution = execution; Identity = identity; }

    internal static async ValueTask<PreparedWorkCleanup> PrepareAsync(RuntimeInstance instance, StateDatabase database,
        InstanceLease lease, ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources,
        ExtensionActivator activator, ResourceOperationLease root, FrozenFileCleanup plan, string subject, bool isNew,
        CancellationToken acceptedCancellation)
    {
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        if (plan.ExtensionWork is not { } work || plan.OperationId != root.OperationId) throw Invalid();
        var borrow = root.Borrow(resources, plan.OperationId, FileCleanupItems.Claims(plan));
        WorkScopeProtection? inventory = null;
        try
        {
            inventory = await WorkScopeProtection.AcquireAsync(activator, database, root, work.Origin,
                acceptedCancellation).ConfigureAwait(false);
            var planReference = new CleanupPlanCodec(instance, contracts, new(instance, contracts), database, roots).Persist(plan);
            var executor = lease.ExecutorIdentity;
            var execution = ExtensionWorkExecution.Owned(instance, database, lease, contracts, roots, work, root,
                borrow, inventory, resources, planReference, executor);
            if (isNew) execution.RequireBeforeAcceptance(plan); else execution.Require(plan);
            if (isNew)
                ExtensionWorkInventory.Require(work.Origin,
                    new ExtensionWorkHistory(instance, database, contracts, roots).RequirePage(work));
            return new(lease, borrow, inventory, plan, execution, ExtensionWorkRequest.Identity(instance, plan.OperationId, subject,
                work.Origin.Proof.ProducerJournalId, work.Origin.Proof.ProducerPlan, work.Page.PreviousJournalId, work.Page.PreviousPlan));
        }
        catch
        {
            try { inventory?.Dispose(); } finally { borrow.Dispose(); }
            throw;
        }
    }

    internal T Publish<T>(Func<FrozenFileCleanup, T> effect)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Execution.RequireBeforeAcceptance(_plan);
        return _lease.PublishExecution(() => _borrow.Use(() =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            Execution.RequireBeforeAcceptance(_plan);
            return effect(_plan);
        }));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _inventory.Dispose(); } finally { _borrow.Dispose(); }
    }
    private static IOException Invalid() => new("extension_work_preparation_unbound");
}
