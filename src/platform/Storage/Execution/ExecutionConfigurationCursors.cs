using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionConfigurationCursor(ConfigurationRevision Source, long BindingRevision, BlobReference Snapshot, string StepId, FrozenRunState State);

internal sealed class ExecutionConfigurationCursors
{
    private readonly ImmutableArray<ExecutionConfigurationCursor> _items;
    private ExecutionConfigurationCursors(ImmutableArray<ExecutionConfigurationCursor> items) => _items = items;

    internal static ExecutionConfigurationCursors Read(RuntimeInstance instance, ContractReader contracts,
        RunAttemptPlan attempt, ExecutionPlan total)
    {
        PlanConfigurationSlots.Validate(total);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var plans = new ConfigurationPlanCodec(instance, contracts, blobs);
        var states = new RunStateCodec(instance, contracts, blobs);
        var sources = new Dictionary<(string StepId, ConfigurationAddress Address), ExecutionConfigurationCursor>();
        foreach (var item in attempt.ConfigurationPlans)
        {
            var child = plans.ReadStructure(AttemptPlanCodec.Reference(item.Plan));
            if (child.Kind != JournalKind.ConfigExchange || child.RunState is null)
                throw new IOException("run_attempt_configuration_state_required");
            var state = states.ReadStructure(child.RunState);
            RunConfigurationBinding.Require(state, item.StepId, child.TargetPath, attempt, total, blobs, contracts);
            if (!sources.TryAdd((item.StepId, state.Source.Address), new(state.Source, state.BindingRevision,
                state.SnapshotReference ?? throw new IOException("configuration_run_snapshot_unbound"), item.StepId, state)))
                throw new IOException("execution_configuration_cursors_unbound");
        }
        if (sources.Count != total.ConfigurationSlots.Count || total.ConfigurationSlots.Any(slot =>
            !sources.ContainsKey((slot.StepId, PlanConfigurationSlots.Address(slot)))))
            throw new IOException("execution_configuration_cursors_unbound");
        return new(total.ConfigurationSlots.Select(slot => sources[(slot.StepId, PlanConfigurationSlots.Address(slot))]).ToImmutableArray());
    }

    internal void RequireHistorical(PreparedRetryOrigin origin)
    {
        foreach (var step in _items.GroupBy(item => item.StepId))
            origin.RequirePrepared(step.Key, step.ToDictionary(item => item.Source.Address, item => item.State));
    }

    internal void RequireSaved(RuntimeInstance instance, NexusPipeline.Platform.Storage.Sqlite.StateDatabase database, ContractReader contracts,
        RunAttemptPlan attempt, ExecutionPlan total)
    {
        foreach (var step in _items.GroupBy(item => item.StepId))
            PreparedRetryOrigin.RequireSaved(instance, database, contracts, attempt, total, step.Key,
                step.ToDictionary(item => item.Source.Address, item => item.State));
    }

    internal void RequireCurrent(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var item in _items)
            ConfigurationRevisionRows.RequireCurrentFrozen(connection, transaction, item.Source, item.BindingRevision, item.Snapshot);
    }
}
