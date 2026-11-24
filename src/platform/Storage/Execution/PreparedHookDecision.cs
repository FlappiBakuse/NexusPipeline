using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedHookDecision : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly AcceptedExecutionAdmission _accepted;
    private readonly ResourceOperationBorrow _borrow;
    private bool _disposed;
    private RecoveryIndex? _index;
    private Func<TimeSpan>? _bindingElapsed;
    private ResourceLeaseManager? _resources;
    private readonly List<IDisposable> _inputs = [];
    internal RetryHookIntent Intent { get; }

    private PreparedHookDecision(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, AcceptedExecutionAdmission accepted, ResourceOperationBorrow borrow, RetryHookIntent intent)
    { _instance = instance; _database = database; _contracts = contracts; _lease = lease; _accepted = accepted; _borrow = borrow; Intent = intent; }

    internal static PreparedHookDecision? Inherit(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, AcceptedExecutionAdmission accepted, string stepId)
    {
        var borrow = Borrow(instance, lease, resources, accepted);
        try
        {
            var proof = borrow.Use<PreparedHookDecision?>(() =>
            {
                var state = AttemptWriterState.Read(instance, database, contracts, accepted.Receipt.JournalId);
                var source = new RetryHookEvidence(instance, database, contracts).FindSuccessfulPre(state, stepId);
                if (source is null) return null;
                var intended = new RetryHookIntent(RetryHookCodec.Format, 1, Guid.Parse(state.Attempt.RunId),
                    Guid.Parse(state.Attempt.AttemptId), state.Journal.JournalId, stepId, "preHook", "inheritedSuccess",
                    state.Latest.Revision, source, null, null);
                new RetryHookEvidence(instance, database, contracts).RequireNew(state, intended);
                return new PreparedHookDecision(instance, database, contracts, lease, accepted, borrow, intended);
            });
            if (proof is null) borrow.Dispose();
            return proof;
        }
        catch { borrow.Dispose(); throw; }
    }

    internal static PreparedHookDecision? DeferPost(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, RecoveryIndex index, AcceptedExecutionAdmission accepted, string stepId, Func<TimeSpan>? bindingElapsed = null)
    {
        var borrow = Borrow(instance, lease, resources, accepted);
        var inputs = new List<IDisposable>();
        try
        {
            PlanInputLease.Acquire(accepted.Prepared.Total.Plan.ReadWire(), inputs);
            var proof = borrow.Use<PreparedHookDecision?>(() =>
            {
                var state = AttemptWriterState.Read(instance, database, contracts, accepted.Receipt.JournalId);
                LiveHookEvidence.Require(database, resources, index, accepted, state);
                if (state.Cancellation is not null || !ExecutionInvocationStore.HasRemainingInvocation(state, stepId) ||
                    !ExecutionInvocationStore.HasOrdinaryFailure(state, stepId)) return null;
                var result = StepResultCodec.Read(state.Latest.Root, state.Total)
                    .SingleOrDefault(result => result.Binding.StepId == stepId);
                if (result is not { IsCollected: true } || result.Latest.Engine == EngineOutcome.NotStarted) return null;
                StepWritersEvidence.RequireStopped(instance, database, contracts, state.Journal.JournalId, state.Jobs);
                AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
                var main = ExecutionBindingRoles.Combine(state.Latest.Root, state.Total,
                    new(stepId, StepCompletionKind.Collected, result, null), state.Attempt.ExecutionPlan.Sha256);
                var now = DateTimeOffset.UtcNow; var budget = accepted.Prepared.TimeBudget.Read();
                if (now < DateTimeOffset.Parse(state.Total.CreatedAt, System.Globalization.CultureInfo.InvariantCulture)) return null;
                var bindingConsumed = bindingElapsed?.Invoke() ?? TimeSpan.Zero;
                var bindingLimit = state.Total.Steps.Single(step => step.StepId == stepId).TotalTimeoutSeconds;
                if (budget.Remaining <= TimeSpan.Zero || budget.ExhaustionReason is not null ||
                    bindingLimit is > 0 && bindingConsumed >= TimeSpan.FromSeconds(bindingLimit.Value)) return null;
                var invocation = ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId);
                var restored = state.Total.ConfigurationSlots.All(slot => slot.StepId != stepId);
                var evidence = new RetryEvidence(AttemptOutcome.Failed, WriterState.Stopped, restored, false, true,
                    checked(state.PreviousInvocationCount(stepId) + invocation), now);
                var decision = new PostDeferralDecision(true, !restored, "execution_invocation_post_deferred");
                var intended = new RetryHookIntent(RetryHookCodec.Format, 1, Guid.Parse(state.Attempt.RunId),
                    Guid.Parse(state.Attempt.AttemptId), state.Journal.JournalId, stepId, "postHook", "deferred", state.Latest.Revision,
                    null, result.Latest.Artifact, new(main, evidence, budget, now, decision) { InvocationOrdinal = invocation, BindingConsumedMilliseconds = bindingConsumed.Ticks / TimeSpan.TicksPerMillisecond });
                new RetryHookEvidence(instance, database, contracts).RequireNew(state, intended);
                var prepared = new PreparedHookDecision(instance, database, contracts, lease, accepted, borrow, intended)
                    { _index = index, _resources = resources, _bindingElapsed = bindingElapsed };
                prepared._inputs.AddRange(inputs); inputs.Clear(); return prepared;
            });
            if (proof is null) borrow.Dispose();
            return proof;
        }
        catch { borrow.Dispose(); throw; }
        finally { for (var i = inputs.Count - 1; i >= 0; i--) inputs[i].Dispose(); }
    }

    internal T Use<T>(Guid parentJournalId, Func<RetryHookIntent, T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _borrow.Use(() =>
        {
            _lease.VerifyHeld(_instance); _accepted.Prepared.RequireLive();
            if (parentJournalId != _accepted.Receipt.JournalId || Intent.ParentJournalId != parentJournalId ||
                _accepted.Prepared.Attempt.Plan.ReadWire().AttemptId != Intent.AttemptId.ToString("D"))
                throw RetryHookEvidence.Invalid();
            return action(Intent);
        });
    }

    internal bool HasCurrentBudget => _accepted.Prepared.TimeBudget.Read().Remaining > TimeSpan.Zero &&
        (_accepted.Prepared.Total.Plan.ReadWire().Steps.Single(step => step.StepId == Intent.StepId).TotalTimeoutSeconds is not > 0 ||
            _bindingElapsed is null || _bindingElapsed() < TimeSpan.FromSeconds(_accepted.Prepared.Total.Plan.ReadWire().Steps.Single(step => step.StepId == Intent.StepId).TotalTimeoutSeconds!.Value));

    internal void RequireNew(AttemptWriterState state)
    {
        if (Intent.Role == "postHook")
            LiveHookEvidence.Require(_database, _resources!, _index!, _accepted, state);
        new RetryHookEvidence(_instance, _database, _contracts).RequireNew(state, Intent);
    }

    private static ResourceOperationBorrow Borrow(RuntimeInstance instance, InstanceLease lease, ResourceLeaseManager resources,
        AcceptedExecutionAdmission accepted)
    {
        lease.VerifyHeld(instance); accepted.Prepared.RequireLive();
        return accepted.Root.Borrow(resources, accepted.Identity.OperationId,
            ExecutionPlanResources.Claims(accepted.Prepared.Total.Plan.ReadWire()));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { for (var i = _inputs.Count - 1; i >= 0; i--) _inputs[i].Dispose(); }
        finally { _borrow.Dispose(); }
    }
}

internal static class LiveHookEvidence
{
    internal static void Require(StateDatabase database, ResourceLeaseManager resources, RecoveryIndex index,
        AcceptedExecutionAdmission accepted, AttemptWriterState state)
    {
        if (!resources.UsesRecoveryIndex(index) || state.Journal.JournalId != accepted.Receipt.JournalId ||
            state.Journal.Phase != JournalPhase.Applying || state.Latest.Revision != state.Journal.Revision)
            throw Invalid();
        var current = index.Current;
        if (!current.IsReady) throw Invalid();
        var claims = ExecutionPlanResources.Claims(state.Total);
        var journals = new HashSet<Guid> { state.Journal.JournalId };
        var operations = new HashSet<Guid> { state.Journal.OperationId };
        foreach (var configuration in accepted.Prepared.Configurations.Values)
            foreach (var resource in configuration.Request.Resources)
            { journals.Add(resource.JournalId); operations.Add(resource.OperationId); }
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT r.journal_id,r.operation_id FROM configuration.configuration_run_resources r JOIN configuration.configuration_run_sessions s USING(session_id) WHERE s.parent_journal_id=$parent;";
            command.Parameters.AddWithValue("$parent", state.Journal.JournalId.ToString("D"));
            using var rows = command.ExecuteReader();
            while (rows.Read()) { journals.Add(Guid.Parse(rows.GetString(0))); operations.Add(Guid.Parse(rows.GetString(1))); }
        }
        var registered = new RecoveryJournalRegistry(database).ReadActiveSnapshot();
        foreach (var journal in registered.Journals.Where(item => journals.Contains(item.JournalId)))
            if (journal.Phase == JournalPhase.Quarantined) throw Invalid();
        if (current.Isolations.Concat(registered.Isolations).Any(item => Conflicts(item.ResourceKey, item.Mode) &&
                (!journals.Contains(item.JournalId) || item.ReasonCode != "recovery_pending")) ||
            current.ResourceHolds.Any(item => Conflicts(item.ResourceKey, item.Mode) &&
                (item.Owner != RecoveryHoldOwner.ConfigurationOperations || !operations.Contains(item.OperationId))) ||
            !ReferenceEquals(current, index.Current))
            throw Invalid();

        bool Conflicts(string key, ResourceClaimMode mode) => claims.Any(claim => claim.ResourceKey == key &&
            (claim.Mode == ResourceClaimMode.Mutation || mode == ResourceClaimMode.Mutation));
    }

    private static IOException Invalid() => new("execution_retry_hook_live_evidence_unverified");
}
