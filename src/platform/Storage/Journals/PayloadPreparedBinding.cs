using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed record BoundPreparedPayload(FrozenPayloadApply Plan, JsonObject Document);

internal static class PayloadPreparedBinding
{
    internal static FrozenPayloadApply ReadPlan(RuntimeInstance instance, ContractReader contracts, PayloadApplyRoots roots, RegisteredJournal journal)
    {
        if (journal.Kind is not (JournalKind.UpdateApply or JournalKind.ExtensionApply)) throw new IOException("payload_kind_unbound");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, journal.PlanPath));
        var plan = new PayloadPlanCodec(instance, contracts, new(instance, contracts), roots)
            .Read(new(journal.PlanPath, new FileInfo(path).Length, journal.PlanDigest));
        if (plan.OperationId != journal.OperationId || plan.JournalId != journal.JournalId || plan.Kind != journal.Kind ||
            !plan.ResourceKeys.SequenceEqual(journal.ResourceKeys)) throw new IOException("payload_plan_registration_unbound");
        return plan;
    }

    internal static BoundPreparedPayload Read(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
        PayloadApplyRoots roots, Guid journalId)
    {
        var snapshot = new RecoveryJournalRegistry(database).ReadSnapshot(journalId);
        RecoveryRegistryPolicy.Verify(snapshot);
        var registered = snapshot.Journals.Single(item => item.JournalId == journalId);
        var plan = ReadPlan(instance, contracts, roots, registered);
        if (plan.Kind == JournalKind.UpdateApply)
        {
            var sourceCodec = new UpdateSourceCodec(instance, contracts);
            var source = sourceCodec.ReadMetadata(plan.MaintenanceSource ?? throw new IOException("maintenance_update_source_required"));
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            sourceCodec.RequireAcceptedInside(connection, transaction, source);
            transaction.Commit();
        }
        var history = new JournalStore(instance, contracts).ReadAllRevisions(journalId);
        var projected = history.SingleOrDefault(item => item.Revision == registered.Revision) ?? throw new IOException("payload_sql_prefix_missing");
        if (projected.Phase != registered.Phase) throw new IOException("payload_sql_phase_unbound");
        var gate = new JournalEffectGate(database).Read(journalId);
        if (gate.InstanceId != instance.InstanceId || gate.OperationId != plan.OperationId || gate.Kind != plan.Kind || gate.PlanDigest != registered.PlanDigest)
            throw new IOException("payload_gate_binding_changed");
        foreach (var physical in history)
        {
            if (physical.Kind != plan.Kind || physical.Root.GetProperty("operationId").GetGuid() != plan.OperationId ||
                physical.PlanDigest != registered.PlanDigest || physical.PlanPath != registered.PlanPath ||
                physical.Root.GetProperty("participants").GetArrayLength() != 0 ||
                !physical.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).SequenceEqual(plan.ResourceKeys))
                throw new IOException("payload_journal_unbound");
            if (physical.Phase is not (JournalPhase.Prepared or JournalPhase.Committed or JournalPhase.Quarantined))
                throw new IOException("payload_nonprepared_recovery_unavailable");
            VerifyReceipts(physical.Root.GetProperty("receipts"));
            var noEffect = HasUnexecutedReceipt(physical.Root);
            if (noEffect && gate.State != EffectGateState.Sealed || physical.Phase == JournalPhase.Committed && !noEffect)
                throw new IOException("payload_no_effect_decision_unbound");
        }
        VerifyEffects(snapshot, plan, registered, history);
        return new(plan, JsonNode.Parse(history[^1].Export())!.AsObject());
    }

    internal static void VerifyPending(RecoveryRegistrySnapshot snapshot, FrozenPayloadApply plan, RegisteredJournal registered)
    {
        var expected = PayloadFileIntents.Create(plan, registered.PlanDigest);
        var actual = snapshot.FileEffects.Where(item => item.JournalId == registered.JournalId).OrderBy(item => item.StepId, StringComparer.Ordinal);
        if (!actual.SequenceEqual(expected.OrderBy(item => item.StepId, StringComparer.Ordinal))) throw new IOException("payload_initial_outbox_unbound");
    }

    private static void VerifyEffects(RecoveryRegistrySnapshot snapshot, FrozenPayloadApply plan, RegisteredJournal registered,
        IReadOnlyList<JournalDocument> history)
    {
        var expected = PayloadFileIntents.Create(plan, registered.PlanDigest);
        var actual = snapshot.FileEffects.Where(item => item.JournalId == registered.JournalId).ToArray();
        if (actual.Length != expected.Count || actual.Any(item => !expected.Any(candidate => candidate.StepId == item.StepId && candidate.Action == item.Action) ||
            item.IntentDigest != registered.PlanDigest || item.State is not (FileEffectState.Pending or FileEffectState.NotExecuted)))
            throw new IOException("payload_outbox_inventory_unbound");
        foreach (var effect in actual.Where(item => item.State == FileEffectState.NotExecuted))
            if (!history.Any(item => Convert.ToHexStringLower(SHA256.HashData(item.Export())) == effect.ReceiptDigest && HasUnexecutedReceipt(item.Root)))
                throw new IOException("payload_outbox_receipt_unbound");
    }

    internal static bool HasUnexecutedReceipt(JsonElement root) => root.GetProperty("receipts").EnumerateArray()
        .Any(item => item.GetProperty("stepId").GetString() == "verify-no-effect" && item.GetProperty("effect").GetString() == "notExecuted");

    private static void VerifyReceipts(JsonElement receipts)
    {
        foreach (var receipt in receipts.EnumerateArray())
        {
            var step = receipt.GetProperty("stepId").GetString();
            var effect = receipt.GetProperty("effect").GetString();
            var detail = receipt.GetProperty("detailCode").GetString();
            var known = (step, effect, detail) is ("verify-no-effect", "notExecuted", "payload_not_executed") ||
                step == "isolate-payload" && effect == "unknown" && detail is "payload_writer_stop_unconfirmed" or "payload_effect_entry_observed" or "payload_prepared_no_effect_unverified";
            if (!known || receipt.TryGetProperty("outcomes", out _)) throw new JsonException("payload_receipt_unrecognized");
        }
    }
}
