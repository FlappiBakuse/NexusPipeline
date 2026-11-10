using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ConfigurationCompletionBinding
{
    internal static IReadOnlyList<SnapshotCompletionIntent> ReadIntents(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, Guid parentJournalId) =>
        SnapshotIntentCodec.Read(AttemptPreparedBinding.Read(instance, contracts, database, parentJournalId).ValidatedRead.History[^1].Root);

    internal static void PrepareIntent(RuntimeInstance instance, StateDatabase database, ContractReader contracts, RegisteredJournal parent,
        Func<SnapshotCompletionIntent> createIntent, Action beforeAppend)
    {
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, parent.JournalId);
        var intents = SnapshotIntentCodec.Read(bound.ValidatedRead.History[^1].Root);
        var intended = createIntent();
        var existing = intents.SingleOrDefault(item => item.SessionId == intended.SessionId);
        if (existing is not null)
        {
            RequireSameIntent(existing, intended);
            return;
        }
        if (intents.Any(item => item.CompletionOperationId == intended.CompletionOperationId)) throw new OperationConflictException();
        var document = bound.Document;
        document["revision"] = parent.Revision + 1;
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["participants"]!.AsArray().Add(SnapshotIntentCodec.Participant(intended));
        document["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = intended.CompletionOperationId.ToString("D"), ["recordedAt"] = document["updatedAt"]!.GetValue<string>(),
            ["stepId"] = SnapshotIntentCodec.Step, ["effect"] = "completed", ["detailCode"] = SnapshotIntentCodec.Reason
        });
        var store = new JournalStore(instance, contracts);
        beforeAppend();
        store.Append(parent.JournalId, parent.Revision, JsonSerializer.SerializeToUtf8Bytes(document));
        new AttemptPreparedProjection(store, new(database), new(database), null).Synchronize(bound.Plan, false);
    }

    private static void RequireSameIntent(SnapshotCompletionIntent existing, SnapshotCompletionIntent intended)
    {
        if (existing.ParentOperationId != intended.ParentOperationId || existing.ParentJournalId != intended.ParentJournalId ||
            existing.ParentPlanDigest != intended.ParentPlanDigest || existing.CompletionOperationId != intended.CompletionOperationId ||
            existing.SubjectId != intended.SubjectId || existing.Terminal != intended.Terminal || existing.GroupRevision != intended.GroupRevision ||
            existing.EvidenceDigest != intended.EvidenceDigest || !existing.SlotKeys.SequenceEqual(intended.SlotKeys)) throw new OperationConflictException();
    }

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
        AttemptTerminalEvidence.RequireStopped(database, parentJournalId);
        JournalResourceClaims.VerifyInventory(instance, database, contracts);
        var references = ExecutionInvocationCodec.Plans(physical.Root, bound.Total, bound.Plan)
            .Select(item => (item.StepId, Reference: AttemptPlanCodec.Reference(item.Plan)));
        var groups = ConfigurationRunInventory.ReadCompletion(instance, database, contracts, parent, registry.Journals, references);
        using var untouched = parent.Phase == JournalPhase.Prepared ? HoldUnenteredEffects(instance, database, contracts, bound.Plan) : null;
        if (parent.Phase == JournalPhase.Applied)
            AttemptTerminalEvidence.RequireCollected(database, AttemptWriterState.Read(instance, database, contracts, parentJournalId));
        AttemptTerminalEvidence.RequireStopped(database, parentJournalId);
        if (new RecoveryJournalRegistry(database).ReadActiveSnapshot().Revision != registry.Revision)
            throw new IOException("attempt_configuration_registry_changed");
        return groups;
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

    private sealed class Held : IDisposable
    {
        internal List<IDisposable> Items { get; } = [];
        public void Dispose() { for (var index = Items.Count - 1; index >= 0; index--) Items[index].Dispose(); Items.Clear(); }
    }
}
