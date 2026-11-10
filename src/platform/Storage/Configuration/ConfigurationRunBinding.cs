using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record BoundRunResource(FrozenConfigurationExchange Plan, FrozenRunState State);
internal sealed record BoundConfigurationRun(RegisteredJournal ParentJournal, RunBindingEvidence Evidence,
    ImmutableArray<BoundRunResource> Resources);

internal static class ConfigurationRunBinding
{
    internal static BoundConfigurationRun Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts, ConfigurationRunStatus session,
        RunBindingEvidence? evidence = null)
    {
        var registered = new RecoveryJournalRegistry(database).ReadSnapshot(session.ParentJournalId).Journals.ToDictionary(item => item.JournalId);
        if (!registered.TryGetValue(session.ParentJournalId, out var parentJournal) || parentJournal.Kind != JournalKind.RunAttempt ||
            parentJournal.OperationId != session.OwnerOperationId) throw new IOException("configuration_run_parent_unbound");
        if (evidence is null) evidence = RunBindingEvidence.Read(instance, database, contracts, session);
        else evidence.RequireInvocation(instance, database, session);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var expected = evidence.Plans.ToHashSet();
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
            evidence.RequireResource(state, plan.TargetPath);
            if (state.Source.Address != resource.Address || state.InitialPublicationRevision != resource.ExpectedRevision || state.Source.Contract != resource.Contract ||
                state.BindingRevision != session.ExpectedBindingRevision) throw new IOException("configuration_run_baseline_unbound");
            resources.Add(new(plan, state));
        }
        if (expected.Count != 0) throw new IOException("configuration_run_parent_coverage_invalid");
        evidence.RequireSources(resources.ToDictionary(item => item.State.Source.Address, item => item.State));
        return new(parentJournal, evidence, resources.ToImmutable());
    }
}
