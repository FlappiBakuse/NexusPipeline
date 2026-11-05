using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
public sealed class RunCompletionService
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _index;
    private readonly ConfigurationRunStore _runs;
    private readonly RunProgressStore _progress;
    private readonly PayloadApplyRoots? _payloadRoots;

    public RunCompletionService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, RecoveryIndex index, PayloadApplyRoots? payloadRoots = null)
    {
        lease.VerifyHeld(instance);
        _payloadRoots = payloadRoots; _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources; _index = index;
        _runs = new(instance, database, contracts); _progress = new(instance, database, contracts);
    }

    public RunSyncResult CompleteInterrupted(Guid operationId, string subject, Guid sessionId)
        => CompleteInterruptedCore(operationId, subject, sessionId, null);

    internal RunSyncResult CompleteOwnedInterrupted(Guid operationId, string subject, Guid sessionId, PreparedConfigurationRun owned)
        => CompleteInterruptedCore(operationId, subject, sessionId, owned);

    private RunSyncResult CompleteInterruptedCore(Guid operationId, string subject, Guid sessionId, PreparedConfigurationRun? owned)
    {
        _lease.VerifyHeld(_instance);
        if (operationId == Guid.Empty || sessionId == Guid.Empty) throw new ArgumentException("configuration_run_progress_identity_invalid");
        var replay = _progress.Lookup(operationId, subject, sessionId, ConfigurationRunPhase.Interrupted);
        if (replay is not null) { ConfigurationRecoveryHolds.Publish(_database, _index, _payloadRoots); return replay; }
        if (!_index.Current.IsReady) throw new ConfigurationException("configuration_host_unavailable");
        var session = _runs.Read(sessionId, subject) ?? throw new ConfigurationException("configuration_run_missing");
        if (session.CompletionOperationId.HasValue) throw new ConfigurationException("configuration_run_revision_conflict");
        _ = _runs.ReadBound(session);
        _ = ConfigurationCompletionBinding.Read(_instance, _database, _contracts, session.ParentJournalId);
        var owner = new SnapshotCompletionOwner(_instance, _database, _contracts, _lease);
        using var scene = owner.HoldRestored(session);
        try
        {
            if (!session.IsWriterAdmissionClosed)
            {
                if (session.Phase != ConfigurationRunPhase.Prepared || session.EnteredRunning || session.SyncOrdinal != 0 ||
                    !new RunStopEvidence(_instance, _database, _contracts).HasNoJobs(session))
                    throw new IOException("configuration_run_stop_duration_missing");
                foreach (var resource in session.Resources)
                    RunStopEvidence.RequireNoEffect(new JournalStore(_instance, _contracts).Read(resource.JournalId));
                new WriterAdmissionStore(_instance, _database, _contracts).CloseRunAdmission(sessionId, 0, requireNoJobs: true);
                session = _runs.Read(sessionId, subject)!;
            }
            owner.Prepare(operationId, subject, session, () => _index.BlockUnknown("configuration_snapshot_completion_preparing"));
            PublishVerified(owned is not null);
            ConfigurationRecoveryHolds.Publish(_database, _index, _payloadRoots);
            var proof = _index.AuthorizeSnapshotCompletion(session.ParentJournalId, sessionId, operationId)
                ?? throw new ConfigurationException("configuration_host_unavailable");
            if (owned is not null)
                return owned.Use(resources =>
                {
                    if (owned.Instance != _instance || owned.OwnerOperationId != session.OwnerOperationId || resources.Count != session.Resources.Length ||
                        resources.Any(item => !session.Resources.Any(resource => resource.Address == item.Address && resource.JournalId == item.Plan.JournalId &&
                            resource.OperationId == item.Plan.OperationId && resource.PlanPath == item.Reference.RelativePath && resource.PlanDigest == item.Reference.Sha256)))
                        throw new IOException("configuration_run_owned_completion_unbound");
                    return PublishCompletion(operationId, subject, session, proof.Completion, owned: true);
                });
            var admission = _resources.TryCompleteSnapshots(proof, Claims(session));
            using var scope = admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
                ? "configuration_resource_conflict" : "configuration_host_unavailable");
            return PublishCompletion(operationId, subject, session, proof.Completion);
        }
        catch { _index.BlockUnknown("configuration_snapshot_completion_reconciliation_required"); throw; }
    }

    private RunSyncResult PublishCompletion(Guid operationId, string subject, ConfigurationRunStatus session,
        SnapshotCompletionProjection completion, bool owned = false)
    {
        using var sample = new RunProgressCapture(_instance, _database, _contracts).ReadRestored(session);
        var result = _progress.Publish(operationId, subject, sample, ConfigurationRunPhase.Interrupted, completion);
        ConfigurationRecoveryHolds.Publish(_database, _index, _payloadRoots); PublishVerified(owned); return result;
    }

    private void PublishVerified(bool owned = false) => _lease.PublishExecution(() => new RecoveryCoordinator(new RecoveryJournalRegistry(_database),
        new SnapshotCompletionEvidence(_instance, _database, _contracts, _payloadRoots ?? (owned ?
            new(PhysicalPathPolicy.RuntimeApplicationDirectory(), Path.Combine(_instance.RuntimeRoot, "extensions")) : null)), _index).VerifyAndPublish());

    private IReadOnlyList<ResourceClaim> Claims(ConfigurationRunStatus session)
    {
        var registry = new RecoveryJournalRegistry(_database).ReadSnapshot(session.ParentJournalId).Journals.ToDictionary(item => item.JournalId);
        var claims = new List<ResourceClaim>();
        foreach (var resource in session.Resources)
        {
            var modes = JournalResourceClaims.Read(_database, resource.JournalId);
            if (!registry[resource.JournalId].ResourceKeys.ToHashSet(StringComparer.Ordinal).SetEquals(modes.Keys))
                throw new IOException("configuration_hold_claims_unbound");
            var key = ConfigurationRevisionService.ResourceKey(resource.Address);
            if (!modes.TryGetValue(key, out var mode) || mode != ResourceClaimMode.Mutation)
                throw new IOException("configuration_hold_claims_unbound");
            claims.Add(new(key, ResourceClaimMode.Mutation));
        }
        return PhysicalResourceAdmission.Merge(claims);
    }
}
