using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed class SecretPreparationJournal(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    FrozenSecretPreparation frozen, BlobReference reference)
{
    private readonly SecretPreparationPlan _plan = frozen.ReadWire();
    private readonly JournalStore _store = new(instance, contracts);
    internal Guid Id => Guid.Parse(_plan.JournalId);
    internal RegisteredJournal Registered() => new RecoveryJournalRegistry(database).ReadJournal(Id);

    internal JournalDocument? ReadOptional()
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", _plan.JournalId));
        if (File.Exists(path)) throw new IOException("secret_journal_shape_invalid");
        if (!Directory.Exists(path))
        {
            if (Registered() is not { Phase: JournalPhase.Prepared, Revision: 1 }) throw new IOException("secret_journal_missing_after_prepared");
            return null;
        }
        var documents = _store.ReadAllRevisions(Id);
        var registered = Registered();
        if (!documents.Any(value => value.Revision == registered.Revision && value.Phase == registered.Phase))
            throw new IOException("secret_journal_sql_prefix_missing");
        foreach (var document in documents) Verify(document);
        return documents[^1];
    }

    internal JournalDocument Materialize()
    {
        var existing = ReadOptional();
        if (existing is not null) return existing;
        var wire = new ExecutionJournal { Format = "nexus.execution.journal", Version = 1, InstanceId = _plan.InstanceId,
            OperationId = _plan.OperationId, JournalId = _plan.JournalId, OperationKind = "secretPrepare", Phase = "prepared", Revision = 1,
            PlanPath = reference.RelativePath, PlanDigest = reference.Sha256, ResourceKeys = _plan.ResourceKeys,
            UpdatedAt = Now(), Participants = [], Receipts = [] };
        return _store.Create(BoundedJson.Serialize(wire));
    }

    internal JournalDocument Append(JournalPhase phase, bool noEffects = false)
    {
        var current = Materialize();
        var wire = contracts.Read<ExecutionJournal>(current.Export(), "execution-journal.schema.json");
        var receipts = wire.Receipts.ToList();
        if (noEffects) receipts.Add(new() { ReceiptId = Guid.NewGuid().ToString("D"), RecordedAt = Now(), StepId = "verify-no-effect",
            Effect = "notExecuted", DetailCode = "secret_preparation_not_entered" });
        wire = wire with { Phase = FileEffectRows.Wire(phase), Revision = current.Revision + 1, UpdatedAt = Now(), Receipts = receipts };
        return _store.Append(Id, current.Revision, BoundedJson.Serialize(wire), noEffects ? new(true, false, false, false) : null);
    }

    internal void ProjectNonterminal()
    {
        var registered = Registered();
        foreach (var document in _store.ReadAllRevisions(Id).Where(value => value.Revision > registered.Revision))
        {
            Verify(document);
            if (document.Phase == JournalPhase.Committed) break;
            new RecoveryJournalRegistry(database).Advance(registered with { Phase = document.Phase, Revision = document.Revision }, registered.Revision);
            registered = Registered();
        }
    }

    internal void Verify(JournalDocument document)
    {
        if (document.Kind != JournalKind.SecretPrepare || document.InstanceId != instance.InstanceId || document.JournalId != Id ||
            document.Root.GetProperty("operationId").GetString() != _plan.OperationId || document.PlanPath != reference.RelativePath ||
            document.PlanDigest != reference.Sha256 || document.Root.GetProperty("participants").GetArrayLength() != 0 ||
            !document.Root.GetProperty("resourceKeys").EnumerateArray().Select(value => value.GetString()).SequenceEqual(_plan.ResourceKeys))
            throw new IOException("secret_journal_plan_unbound");
        var receipts = document.Root.GetProperty("receipts");
        if (receipts.GetArrayLength() > 1 || receipts.EnumerateArray().Any(value => value.GetProperty("stepId").GetString() != "verify-no-effect" ||
            value.GetProperty("effect").GetString() != "notExecuted" || value.GetProperty("detailCode").GetString() != "secret_preparation_not_entered"))
            throw new IOException("secret_journal_receipt_unbound");
    }

    internal static bool NoEffects(JournalDocument? document) => document is not null && document.Root.GetProperty("receipts").GetArrayLength() == 1;
    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
}
