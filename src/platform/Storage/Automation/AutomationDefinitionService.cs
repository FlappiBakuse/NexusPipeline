using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed class AutomationDefinitionService(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    InstanceLease instanceLease, ResourceLeaseManager resources, RecoveryIndex index, NexusPipeline.Platform.Storage.Journals.PayloadApplyRoots? payloadRoots = null)
{
    public AutomationCatalog Read() => new AutomationDefinitionStore(database).Read();
    public long ReadRevision() => new AutomationDefinitionStore(database).ReadRevision();

    public AutomationChangeResult? ReadAccepted(OperationIdentity operation, AutomationChanges changes)
        => ReadAccepted(operation, changes, null);

    internal AutomationChangeResult? ReadAccepted(OperationIdentity operation, AutomationChanges changes, IReadOnlyList<Guid>? drafts)
    {
        instanceLease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("automation_service_instance_unbound");
        return new AutomationDefinitionStore(database).Lookup(operation, changes, drafts);
    }

    public AutomationChangeResult Change(OperationIdentity operation, AutomationChanges changes, DateTimeOffset acceptedAt)
        => Change(operation, changes, acceptedAt, null, null);

    internal AutomationChangeResult Change(OperationIdentity operation, AutomationChanges changes, DateTimeOffset acceptedAt,
        IReadOnlyList<Guid>? ids, BindingDrafts? drafts)
    {
        instanceLease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("automation_service_instance_unbound");
        var store = new AutomationDefinitionStore(database);
        var existing = store.Lookup(operation, changes, ids);
        if (existing is not null) return existing;
        var claims = changes.Scripts.Where(script => script.ExtensionId is not null).Select(script =>
            new ResourceClaim("extension:" + script.ExtensionId, ResourceClaimMode.Reference))
            .Append(new("automation:definitions", ResourceClaimMode.Mutation));
        if (ids is not null) claims = claims.Concat(drafts!.Claims(ids, operation.SubjectId));
        var admission = instanceLease.PublishExecution(() => resources.TryAcquire(operation.OperationId, claims.Distinct().ToArray()));
        using var lease = admission.Lease ?? throw new AutomationDefinitionException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "automation_resource_conflict" : "automation_host_unavailable");
        try
        {
            SpecializedScriptPolicy.Require(instance, database, contracts, payloadRoots, changes.Scripts);
            var result = ids is null ? store.Commit(operation, changes, acceptedAt)
                : store.CommitDrafts(operation, changes, acceptedAt, ids, drafts!);
            if (!result.CleanupJournalIds.IsEmpty)
            {
                var evidence = new JournalRecoveryEvidence(instance, new(instance, contracts), database);
                new RecoveryCoordinator(new RecoveryJournalRegistry(database), evidence, index).VerifyAndPublish();
            }
            return result;
        }
        catch
        {
            try
            {
                if (new RecoveryJournalRegistry(database).ReadHistory().Any(value => value.OperationId == operation.OperationId))
                    index.BlockUnknown("automation_cleanup_reconciliation_required");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                { index.BlockUnknown("automation_cleanup_reconciliation_required"); }
            throw;
        }
    }
}
