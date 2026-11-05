using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record BoundRunResource(FrozenConfigurationExchange Plan, FrozenRunState State);
internal sealed record BoundConfigurationRun(RegisteredJournal ParentJournal, RunAttemptPlan Parent, ExecutionPlanStep Step,
    ImmutableArray<BoundRunResource> Resources);

internal static class ConfigurationRunBinding
{
    internal static BoundConfigurationRun Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts, ConfigurationRunStatus session,
        RunAttemptPlan? verifiedParent = null, ExecutionPlan? verifiedTotal = null)
    {
        var registered = new RecoveryJournalRegistry(database).ReadSnapshot(session.ParentJournalId).Journals.ToDictionary(item => item.JournalId);
        if (!registered.TryGetValue(session.ParentJournalId, out var parentJournal) || parentJournal.Kind != JournalKind.RunAttempt ||
            parentJournal.OperationId != session.OwnerOperationId) throw new IOException("configuration_run_parent_unbound");
        var parent = verifiedParent ?? AttemptPreparedBinding.Read(instance, contracts, database, parentJournal.JournalId).Plan;
        if (parent.JournalId != session.ParentJournalId.ToString("D") || parent.OperationId != session.OwnerOperationId.ToString("D"))
            throw new IOException("configuration_run_parent_unbound");
        var blobs = new ImmutableBlobStore(instance, contracts);
        var total = verifiedTotal ?? new ExecutionPlanCodec(instance, contracts, blobs).Read(AttemptPlanCodec.Reference(parent.ExecutionPlan)).ReadWire();
        var step = total.Steps.SingleOrDefault(item => item.StepId == session.StepId) ?? throw new IOException("configuration_run_step_unknown");
        var latest = new JournalStore(instance, contracts).Read(session.ParentJournalId);
        var taskRetry = ExecutionInvocationCodec.Read(latest.Root, total).SingleOrDefault(item =>
            item.StepId == session.StepId && item.Ordinal == session.InvocationOrdinal)?.TaskRetry;
        var expected = ExecutionInvocationCodec.PlansForOrdinal(latest.Root,
            total, parent, session.StepId, session.InvocationOrdinal).ToHashSet();
        var resources = ImmutableArray.CreateBuilder<BoundRunResource>();
        var codec = new ConfigurationPlanCodec(instance, contracts, blobs);
        foreach (var resource in session.Resources)
        {
            var path = Path.Combine(instance.RuntimeRoot, resource.PlanPath);
            var reference = new BlobReference(resource.PlanPath, new FileInfo(path).Length, resource.PlanDigest);
            if (!expected.Remove(reference)) throw new IOException("configuration_run_parent_coverage_invalid");
            var plan = codec.ReadStructure(reference);
            if (plan.RunState is null || plan.EditPolicy is null || plan.Editor is not null || plan.PhysicalClaims.IsDefaultOrEmpty ||
                plan.Kind != JournalKind.ConfigExchange || plan.OperationId != resource.OperationId || plan.JournalId != resource.JournalId ||
                !registered.TryGetValue(resource.JournalId, out var journal) || journal.OperationId != resource.OperationId || journal.Kind != plan.Kind ||
                journal.PlanPath != resource.PlanPath || journal.PlanDigest != resource.PlanDigest ||
                !journal.ResourceKeys.Contains(ConfigurationRevisionService.ResourceKey(resource.Address), StringComparer.Ordinal))
                throw new IOException("configuration_run_resource_unbound");
            var state = new RunStateCodec(instance, contracts, blobs).ReadStructure(plan.RunState);
            RunConfigurationBinding.Require(state, session.StepId, plan.TargetPath, parent, total, blobs, contracts, laterInvocation: session.InvocationOrdinal > 1, taskRetry: taskRetry);
            if (state.Source.Address != resource.Address || state.InitialPublicationRevision != resource.ExpectedRevision || state.Source.Contract != resource.Contract ||
                state.BindingRevision != session.ExpectedBindingRevision) throw new IOException("configuration_run_baseline_unbound");
            resources.Add(new(plan, state));
        }
        if (expected.Count != 0) throw new IOException("configuration_run_parent_coverage_invalid");
        PreparedRetryOrigin.RequireSaved(instance, database, contracts, parent, total, session.StepId,
            resources.ToDictionary(item => item.State.Source.Address, item => item.State));
        return new(parentJournal, parent, step, resources.ToImmutable());
    }
}
