using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class EditCompletionService
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _instanceLease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _index;
    private readonly ConfigurationEditStore _store;

    public EditCompletionService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease instanceLease, ResourceLeaseManager resources, RecoveryIndex index)
    {
        instanceLease.VerifyHeld(instance);
        _instance = instance; _database = database; _instanceLease = instanceLease;
        _resources = resources; _index = index; _store = new(instance, database, contracts);
    }

    public ConfigurationBatchResult Save(OperationIdentity operation, ConfigurationEditCompletion completion, DateTimeOffset acceptedAt)
    {
        _instanceLease.VerifyHeld(_instance);
        completion = ConfigurationEditPolicy.Freeze(completion);
        var replay = _store.LookupSave(operation, completion);
        if (replay is not null) { PublishHolds(); return replay; }
        var session = Required(completion.SessionId, operation.SubjectId);
        ConfigurationEditPolicy.RequireCompletion(session, completion);
        using var lease = Acquire(session);
        var saved = _store.Save(operation, completion, acceptedAt);
        PublishHolds();
        return saved;
    }

    public ConfigurationEditStatus Cancel(OperationIdentity operation, ConfigurationEditCancel cancel, DateTimeOffset acceptedAt)
    {
        _instanceLease.VerifyHeld(_instance);
        var replay = _store.LookupCancel(operation, cancel);
        if (replay is not null) { PublishHolds(); return replay; }
        var session = Required(cancel.SessionId, operation.SubjectId);
        ConfigurationEditStore.RequireOpen(session, cancel.ExpectedSessionRevision);
        using var lease = Acquire(session);
        var cancelled = _store.Cancel(operation, cancel, acceptedAt);
        PublishHolds();
        return cancelled;
    }

    private ResourceOperationLease Acquire(ConfigurationEditStatus session)
    {
        var registry = new RecoveryJournalRegistry(_database);
        if (session.Resources.Any(resource => registry.FindJournal(resource.JournalId) is not { } journal ||
            journal.OperationId != session.SessionId || journal.Kind != JournalKind.ConfigEdit || journal.Phase != JournalPhase.Committed ||
            journal.PlanPath != resource.PlanPath || journal.PlanDigest != resource.PlanDigest))
            throw new ConfigurationException("configuration_edit_restoration_required");
        var claims = session.Resources.Select(resource => new ResourceClaim(ConfigurationRevisionService.ResourceKey(resource.Address),
            ResourceClaimMode.Mutation)).ToArray();
        var continuation = _index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, session.SessionId,
            claims.Select(item => item.ResourceKey).ToHashSet(StringComparer.Ordinal))
            ?? throw new ConfigurationException("configuration_host_unavailable");
        var admission = _resources.TryContinue(continuation, claims);
        return admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "configuration_resource_conflict" : "configuration_host_unavailable");
    }

    private ConfigurationEditStatus Required(Guid sessionId, string subject) =>
        _store.Read(sessionId, subject) ?? throw new ConfigurationException("configuration_edit_missing");

    private void PublishHolds()
    {
        try { ConfigurationEditRecovery.PublishHolds(_database, _index); }
        catch { _index.BlockUnknown("configuration_edit_holds_publication_required"); throw; }
    }
}
