using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed record BoundFileCleanup(FrozenFileCleanup Plan, JsonObject Document);

internal static class CleanupJournalBinding
{
    internal static FrozenFileCleanup ReadPlan(RuntimeInstance instance, ContractReader contracts, RegisteredJournal journal,
        StateDatabase? database = null, PayloadApplyRoots? roots = null)
    {
        var plan = ReadPagePlan(instance, contracts, journal, database, roots);
        RequirePreviousPages(instance, contracts, plan, database, roots);
        return plan;
    }

    private static FrozenFileCleanup ReadPagePlan(RuntimeInstance instance, ContractReader contracts, RegisteredJournal journal,
        StateDatabase? database, PayloadApplyRoots? roots)
    {
        if (journal.Kind != JournalKind.FileCleanup) throw new IOException("cleanup_kind_unbound");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, journal.PlanPath));
        var plan = new CleanupPlanCodec(instance, contracts, new(instance, contracts), database, roots)
            .Read(new(journal.PlanPath, new FileInfo(path).Length, journal.PlanDigest));
        if (plan.OperationId != journal.OperationId || plan.JournalId != journal.JournalId ||
            !journal.ResourceKeys.SequenceEqual(FileCleanupItems.Keys(plan)))
            throw new IOException("cleanup_plan_registration_unbound");
        if (plan.ExtensionWork is not null)
        {
            var claims = JournalResourceClaims.Read(database ?? throw new IOException("extension_work_database_required"), journal.JournalId);
            var expected = FileCleanupItems.Claims(plan);
            if (claims.Count != expected.Count || expected.Any(item => !claims.TryGetValue(item.ResourceKey, out var mode) || mode != item.Mode))
                throw new IOException("extension_work_claims_unbound");
        }
        return plan;
    }

    internal static BoundFileCleanup Read(RuntimeInstance instance, ContractReader contracts, RecoveryRegistrySnapshot snapshot, Guid journalId,
        StateDatabase? database = null, PayloadApplyRoots? roots = null) =>
        ReadExact(instance, contracts, snapshot.Journals.Single(item => item.JournalId == journalId),
            snapshot.FileEffects.Where(item => item.JournalId == journalId).ToArray(), database, roots);

    internal static BoundFileCleanup ReadExact(RuntimeInstance instance, ContractReader contracts, RegisteredJournal registered,
        IReadOnlyList<FileEffect> effects, StateDatabase? database = null, PayloadApplyRoots? roots = null)
    {
        var bound = ReadHistoryPage(instance, contracts, registered, effects, database, roots);
        RequirePreviousPages(instance, contracts, bound.Plan, database, roots);
        return bound;
    }

    internal static BoundFileCleanup ReadHistoryPage(RuntimeInstance instance, ContractReader contracts, RegisteredJournal registered,
        IReadOnlyList<FileEffect> effects, StateDatabase? database, PayloadApplyRoots? roots)
    {
        var journalId = registered.JournalId;
        var plan = ReadPagePlan(instance, contracts, registered, database, roots);
        var store = new JournalStore(instance, contracts);
        var history = store.ReadAllRevisions(journalId);
        var projected = history.SingleOrDefault(item => item.Revision == registered.Revision) ?? throw new IOException("cleanup_sql_prefix_missing");
        if (projected.Phase != registered.Phase) throw new IOException("cleanup_sql_phase_unbound");
        var steps = FileCleanupItems.Steps(plan).ToHashSet(StringComparer.Ordinal);
        var byDigest = new Dictionary<string, JournalDocument>(StringComparer.Ordinal);
        foreach (var physical in history)
        {
            if (physical.Kind != JournalKind.FileCleanup || physical.Root.GetProperty("operationId").GetGuid() != plan.OperationId ||
                physical.PlanPath != registered.PlanPath || physical.PlanDigest != registered.PlanDigest ||
                physical.Root.GetProperty("participants").GetArrayLength() != 0 ||
                !physical.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).SequenceEqual(registered.ResourceKeys))
                throw new IOException("cleanup_journal_unbound");
            VerifyReceipts(steps, physical.Root.GetProperty("receipts"));
            if (!byDigest.TryAdd(Convert.ToHexStringLower(SHA256.HashData(physical.Export())), physical))
                throw new IOException("cleanup_history_digest_ambiguous");
        }
        if (effects.Count != steps.Count || effects.Any(effect => effect.Action != FileEffectAction.RemoveOwned ||
            effect.IntentDigest != registered.PlanDigest || !steps.Contains(effect.StepId)))
            throw new IOException("cleanup_effect_inventory_unbound");
        foreach (var effect in effects.Where(item => item.State != FileEffectState.Pending))
        {
            if (effect.ReceiptDigest is not { } digest || !byDigest.TryGetValue(digest, out var evidence))
                throw new IOException("cleanup_outbox_receipt_missing");
            var expectedEffect = effect.State switch
            {
                FileEffectState.Started => "started", FileEffectState.Completed => "completed", FileEffectState.NotExecuted => "notExecuted",
                _ => throw new IOException("cleanup_outbox_state_unrecognized")
            };
            if (!evidence.Root.GetProperty("receipts").EnumerateArray().Any(item => item.GetProperty("effect").GetString() == expectedEffect &&
                (item.GetProperty("stepId").GetString() == effect.StepId ||
                    expectedEffect == "notExecuted" && item.GetProperty("stepId").GetString() == "verify-no-effect")))
                throw new IOException("cleanup_outbox_receipt_unbound");
        }
        return new(plan, JsonNode.Parse(history[^1].Export())!.AsObject());
    }

    private static void RequirePreviousPages(RuntimeInstance instance, ContractReader contracts, FrozenFileCleanup plan,
        StateDatabase? database, PayloadApplyRoots? roots)
    {
        if (plan.ExtensionWork is not { } work) return;
        _ = new ExtensionWorkHistory(instance, database ?? throw new IOException("extension_work_database_required"),
            contracts, roots ?? throw new IOException("extension_work_roots_required")).RequirePage(work);
    }

    private static void VerifyReceipts(IReadOnlySet<string> steps, JsonElement receipts)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var receipt in receipts.EnumerateArray())
        {
            var step = receipt.GetProperty("stepId").GetString()!;
            var effect = receipt.GetProperty("effect").GetString()!;
            var detail = receipt.GetProperty("detailCode").GetString()!;
            var known = (step, effect, detail) is ("begin-cleanup", "started", "cleanup_applying") or
                ("finish-cleanup", "completed", "cleanup_applied") or ("commit-cleanup", "completed", "cleanup_committed") or
                ("verify-no-effect", "notExecuted", "cleanup_no_effect") ||
                step == "isolate-cleanup" && effect == "unknown";
            if (steps.Contains(step))
            {
                states.TryGetValue(step, out var before);
                known = before is null && (effect, detail) is ("started", "cleanup_removal_started") or ("notExecuted", "cleanup_referenced") ||
                    before == "started" && (effect, detail) is ("completed", "cleanup_deleted") or ("notExecuted", "cleanup_referenced");
                if (known) states[step] = effect;
            }
            if (!known || receipt.TryGetProperty("outcomes", out _)) throw new JsonException("cleanup_receipt_unrecognized");
        }
    }
}

