using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ConfigurationCompletionBinding
{
    internal static ImmutableArray<ConfigurationRunStatus> Read(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, Guid parentJournalId)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("attempt_configuration_instance_unbound");
        var registry = new RecoveryJournalRegistry(database).ReadSnapshot(parentJournalId);
        RecoveryRegistryPolicy.Verify(registry);
        new JournalRecoveryEvidence(instance, new(instance, contracts), database).VerifyRecognizedScope(registry.Journals);
        var parent = registry.Journals.Single(item => item.JournalId == parentJournalId);
        if (parent.Kind != JournalKind.RunAttempt || parent.Phase is not (JournalPhase.Prepared or JournalPhase.Applied))
            throw new IOException("attempt_configuration_parent_unbound");
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, parentJournalId);
        var physical = new JournalStore(instance, contracts).Read(parentJournalId);
        if (physical.Phase != parent.Phase || physical.Revision != parent.Revision || NoEffectReceipt.Recorded(physical.Root))
            throw new IOException("attempt_configuration_parent_unbound");
        var gate = new JournalEffectGate(database).Read(parentJournalId);
        if ((parent.Phase == JournalPhase.Prepared ? gate.State != EffectGateState.Sealed : gate.State != EffectGateState.Entered) ||
            gate.OperationId != parent.OperationId || gate.PlanDigest != parent.PlanDigest)
            throw new IOException("attempt_configuration_gate_unverified");
        var writers = new JournalWriterStore(database);
        RequireStopped(writers, parentJournalId);
        JournalResourceClaims.VerifyInventory(instance, database, contracts);
        var groups = ReadGroups(database, parent);
        var plans = new ConfigurationPlanCodec(instance, contracts, new(instance, contracts));
        var expected = ExecutionInvocationCodec.Plans(physical.Root, bound.Total, bound.Plan).Select(item =>
        {
            var reference = AttemptPlanCodec.Reference(item.Plan);
            var child = plans.ReadStructure(reference);
            return (item.StepId, Plan: child, Reference: reference);
        }).ToDictionary(item => item.Plan.JournalId);
        var covered = new HashSet<Guid>();
        foreach (var group in groups)
        {
            var binding = ConfigurationRunBinding.Read(instance, database, contracts, group);
            if (binding.ParentJournal.JournalId != parentJournalId || binding.ParentJournal.OperationId != parent.OperationId ||
                binding.ParentJournal.Phase != parent.Phase || binding.ParentJournal.Revision != parent.Revision ||
                binding.ParentJournal.PlanDigest != parent.PlanDigest)
                throw new IOException("attempt_configuration_parent_unbound");
            foreach (var resource in group.Resources)
            {
                if (!expected.TryGetValue(resource.JournalId, out var child) || child.StepId != group.StepId ||
                    child.Plan.OperationId != resource.OperationId || child.Reference.RelativePath != resource.PlanPath ||
                    child.Reference.Sha256 != resource.PlanDigest || !covered.Add(resource.JournalId))
                    throw new IOException("attempt_configuration_membership_unbound");
                var journal = registry.Journals.Single(item => item.JournalId == resource.JournalId);
                if (journal.Phase == JournalPhase.Quarantined) throw new IOException("attempt_configuration_child_quarantined");
                RequireStopped(writers, resource.JournalId);
                var journalPath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", resource.JournalId.ToString("D")));
                if (!Directory.Exists(journalPath))
                {
                    if (File.Exists(journalPath)) throw new IOException("attempt_configuration_child_projection_unbound");
                    new InitialConfigurationJournal(instance, contracts, database).VerifyPreparedPlan(journal);
                    continue;
                }
                var physicalChild = new JournalStore(instance, contracts).Read(resource.JournalId);
                if (physicalChild.Phase == JournalPhase.Quarantined)
                    throw new IOException("attempt_configuration_child_quarantined");
                _ = ConfigurationJournalBinding.Read(instance, physicalChild, contracts, database);
            }
        }
        if (groups.IsEmpty || registry.Journals.Where(item => expected.ContainsKey(item.JournalId)).Any(item => !covered.Contains(item.JournalId)))
            throw new IOException("attempt_configuration_membership_unbound");
        foreach (var journal in registry.Journals.Where(item => item.Kind == JournalKind.ConfigExchange))
        {
            var child = plans.ReadStructure(new(journal.PlanPath, new FileInfo(Path.Combine(instance.RuntimeRoot, journal.PlanPath)).Length, journal.PlanDigest));
            if (child.RunState is null) continue;
            var state = new RunStateCodec(instance, contracts, new(instance, contracts)).ReadStructure(child.RunState);
            if (state.OwnerOperationId == parent.OperationId && (!expected.ContainsKey(journal.JournalId) || !covered.Contains(journal.JournalId)))
                throw new IOException("attempt_configuration_membership_unbound");
        }
        using var untouched = parent.Phase == JournalPhase.Prepared ? HoldUnenteredEffects(instance, database, contracts, bound.Plan) : null;
        if (parent.Phase == JournalPhase.Applied)
            AttemptTerminalEvidence.RequireCollected(database, AttemptWriterState.Read(instance, database, contracts, parentJournalId));
        RequireStopped(writers, parentJournalId);
        if (new RecoveryJournalRegistry(database).ReadActiveSnapshot().Revision != registry.Revision)
            throw new IOException("attempt_configuration_registry_changed");
        return groups;
    }

    private static ImmutableArray<ConfigurationRunStatus> ReadGroups(StateDatabase database, RegisteredJournal parent)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id FROM operations WHERE operation_id=$owner AND status='accepted';";
        command.Parameters.AddWithValue("$owner", parent.OperationId.ToString("D"));
        var subject = command.ExecuteScalar() as string ?? throw new IOException("attempt_configuration_parent_operation_unbound");
        command.CommandText = "SELECT s.session_id,o.subject_id,o.operation_kind,o.status FROM configuration_run_sessions s " +
            "JOIN operations o ON o.operation_id=s.session_id WHERE s.parent_journal_id=$parent OR s.owner_operation_id=$owner LIMIT 4097;";
        command.Parameters.AddWithValue("$parent", parent.JournalId.ToString("D"));
        var ids = new List<Guid>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                if (rows.GetString(1) != subject || rows.GetString(2) != "configuration.run.begin" || ids.Count >= 4096)
                    throw new IOException("attempt_configuration_group_unbound");
                ids.Add(Guid.Parse(rows.GetString(0)));
            }
        var groups = ImmutableArray.CreateBuilder<ConfigurationRunStatus>();
        foreach (var id in ids)
        {
            var group = ConfigurationRunRows.Read(connection, transaction, id, subject)!;
            if (group.OwnerOperationId != parent.OperationId || group.ParentJournalId != parent.JournalId)
                throw new IOException("attempt_configuration_group_unbound");
            command.CommandText = "SELECT status FROM operations WHERE operation_id=$group;";
            command.Parameters.AddWithValue("$group", id.ToString("D"));
            if (command.ExecuteScalar() as string != (group.Phase == ConfigurationRunPhase.Prepared ? "accepted" : "completed"))
                throw new IOException("attempt_configuration_group_operation_unbound");
            command.Parameters.RemoveAt("$group");
            foreach (var resource in group.Resources)
            {
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_id=$child AND subject_id=$subject " +
                    "AND operation_kind='configuration.run.resource' AND request_digest=$digest AND status=$status);";
                command.Parameters.AddWithValue("$child", resource.OperationId.ToString("D"));
                command.Parameters.AddWithValue("$subject", subject); command.Parameters.AddWithValue("$digest", resource.PlanDigest);
                command.Parameters.AddWithValue("$status", group.CompletionOperationId.HasValue ? "completed" : "accepted");
                if ((long)command.ExecuteScalar()! != 1) throw new IOException("attempt_configuration_child_operation_unbound");
                foreach (var key in new[] { "$child", "$subject", "$digest", "$status" }) command.Parameters.RemoveAt(key);
            }
            groups.Add(group);
        }
        return groups.ToImmutable();
    }

    private static IDisposable HoldUnenteredEffects(RuntimeInstance instance, StateDatabase database, ContractReader contracts, RunAttemptPlan plan)
    {
        var held = new Held();
        try
        {
            foreach (var log in plan.InitialLogs)
            {
                held.Items.Add(InitialLogLease.Acquire(instance, database, contracts, plan, log, recognizeConfigurationScenes: true));
            }
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            foreach (var path in new[] { plan.OutputDirectory, plan.LogDirectory })
                if (scanner.Capture(Path.Combine(instance.RuntimeRoot, path), default).Shape != FileObjectKind.Missing)
                    throw new IOException("attempt_configuration_nonconfiguration_effect_observed");
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private static void RequireStopped(JournalWriterStore writers, Guid journal)
    {
        var observed = writers.ObserveForCurrentExecutor(journal);
        if (!observed.IsComplete || observed.State != WriterState.Stopped) throw new IOException("attempt_writer_stop_unconfirmed");
    }

    private sealed class Held : IDisposable
    {
        internal List<IDisposable> Items { get; } = [];
        public void Dispose() { for (var index = Items.Count - 1; index >= 0; index--) Items[index].Dispose(); Items.Clear(); }
    }
}
