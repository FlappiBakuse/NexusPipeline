using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class UnstartedConfigurationEvidence
{
    internal static IDisposable Hold(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, IEnumerable<string> selectedSteps)
    {
        var held = new Held();
        try
        {
            var selected = selectedSteps.ToHashSet(StringComparer.Ordinal);
            var registered = new RecoveryJournalRegistry(database).ReadSnapshot(state.Journal.JournalId).Journals.Select(item => item.JournalId).ToHashSet();
            var plans = new ConfigurationPlanCodec(instance, contracts, new(instance, contracts));
            var scenes = new List<ConfigurationSceneResource>();
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            foreach (var item in ExecutionInvocationCodec.Plans(state.Latest.Root, state.Total, state.Attempt).Where(item => selected.Contains(item.StepId)))
            {
                var reference = AttemptPlanCodec.Reference(item.Plan); var child = plans.ReadStructure(reference);
                var run = new RunStateCodec(instance, contracts, new(instance, contracts)).ReadStructure(
                    child.RunState ?? throw new IOException("run_attempt_configuration_state_required"));
                if (registered.Contains(child.JournalId))
                    scenes.Add(new(child.OperationId, child.JournalId, child.Kind, run.Source.Address, reference.RelativePath, reference.Sha256));
                else
                {
                    using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration_run_sessions WHERE parent_journal_id=$parent AND step_id=$step AND invocation_ordinal=$invocation);";
                    command.Parameters.AddWithValue("$parent", state.Journal.JournalId.ToString("D")); command.Parameters.AddWithValue("$step", item.StepId);
                    command.Parameters.AddWithValue("$invocation", ExecutionInvocationCodec.PlanOrdinal(state.Latest.Root, state.Total, state.Attempt, item.StepId, reference));
                    if ((long)command.ExecuteScalar()! != 0 || scanner.Capture(Path.Combine(instance.RuntimeRoot, "journals", child.JournalId.ToString("D")), default).Shape != FileObjectKind.Missing ||
                        scanner.Capture(Path.Combine(Path.GetDirectoryName(child.TargetPath)!, ".nexus-" + child.JournalId.ToString("D")), default).Shape != FileObjectKind.Missing)
                        throw new IOException("execution_unstarted_configuration_unverified");
                    held.Add(TreeReadLease.Acquire(child.TargetPath, child.ParentIdentity, child.Before));
                }
            }
            held.Add(new ConfigurationSceneEvidence(instance, database, contracts).Hold(scenes, installed: false));
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private sealed class Held : IDisposable
    {
        private readonly List<IDisposable> _leases = [];
        internal void Add(IDisposable value) => _leases.Add(value);
        public void Dispose() { for (var index = _leases.Count - 1; index >= 0; index--) _leases[index].Dispose(); }
    }
}
