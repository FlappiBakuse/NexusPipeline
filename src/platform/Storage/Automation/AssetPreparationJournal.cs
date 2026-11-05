using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

internal sealed class AssetPreparationJournal(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    FrozenAssetPreparation frozen, BlobReference reference)
{
    private readonly AssetPreparationPlan _plan = frozen.ReadWire();
    private readonly JournalStore _store = new(instance, contracts);
    private readonly RecoveryJournalRegistry _registry = new(database);
    internal Guid Id => Guid.Parse(_plan.JournalId);

    internal RegisteredJournal Registered() => _registry.ReadJournal(Id);

    internal JournalDocument? ReadOptional()
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", _plan.JournalId));
        if (File.Exists(path)) throw new IOException("asset_journal_shape_invalid");
        if (!Directory.Exists(path))
        {
            var registered = Registered();
            if (registered.Phase != JournalPhase.Prepared || registered.Revision != 1) throw new IOException("asset_journal_missing_after_prepared");
            return null;
        }
        var document = _store.Read(Id);
        Verify(document);
        return document;
    }

    internal JournalDocument Materialize()
    {
        var existing = ReadOptional();
        if (existing is not null) return existing;
        var wire = new ExecutionJournal
        {
            Format = "nexus.execution.journal", Version = 1, InstanceId = _plan.InstanceId, OperationId = _plan.OperationId,
            JournalId = _plan.JournalId, OperationKind = "assetPrepare", Phase = "prepared", Revision = 1,
            PlanPath = reference.RelativePath, PlanDigest = reference.Sha256, ResourceKeys = _plan.ResourceKeys,
            UpdatedAt = Now(), Participants = [], Receipts = []
        };
        return _store.Create(BoundedJson.Serialize(wire));
    }

    internal JournalDocument Append(JournalPhase phase, AssetPreparationWorkspace? workspace = null, bool noEffects = false)
    {
        var current = Materialize();
        var wire = contracts.Read<ExecutionJournal>(current.Export(), "execution-journal.schema.json");
        var participants = wire.Participants.ToList();
        if (workspace is not null)
        {
            var verified = new AssetEvidenceCodec(instance, contracts).DecodeWorkspace(BoundedJson.Serialize(workspace), _plan);
            participants.Add(new() { ParticipantId = _plan.JournalId, Kind = "directory", ResourceKey = "automation:definitions",
                Identity = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(BoundedJson.Serialize(verified))! });
        }
        var receipts = wire.Receipts.ToList();
        if (noEffects) receipts.Add(new() { ReceiptId = Guid.NewGuid().ToString("D"), RecordedAt = Now(), StepId = "verify-no-effect",
            Effect = "notExecuted", DetailCode = "asset_preparation_not_entered" });
        wire = wire with { Phase = FileEffectRows.Wire(phase), Revision = checked(current.Revision + 1), UpdatedAt = Now(), Participants = participants, Receipts = receipts };
        return _store.Append(Id, current.Revision, BoundedJson.Serialize(wire), noEffects ? new(true, false, false, false) : null);
    }

    internal AssetPreparationWorkspace? Workspace(JournalDocument? document)
    {
        if (document is null) return null;
        var participants = document.Root.GetProperty("participants");
        if (participants.GetArrayLength() == 0) return null;
        if (participants.GetArrayLength() != 1) throw new IOException("asset_journal_participants_unbound");
        var item = participants[0];
        if (item.GetProperty("participantId").GetString() != _plan.JournalId || item.GetProperty("kind").GetString() != "directory" ||
            item.GetProperty("resourceKey").GetString() != "automation:definitions") throw new IOException("asset_journal_workspace_unbound");
        return new AssetEvidenceCodec(instance, contracts).DecodeWorkspace(BoundedJson.Serialize(item.GetProperty("identity")), _plan);
    }

    internal void ProjectNonterminal()
    {
        var registered = Registered();
        foreach (var document in _store.ReadAllRevisions(Id).Where(value => value.Revision > registered.Revision))
        {
            Verify(document);
            if (document.Phase == JournalPhase.Committed) break;
            var next = registered with { Phase = document.Phase, Revision = document.Revision };
            _registry.Advance(next, registered.Revision);
            registered = next;
        }
    }

    private void Verify(JournalDocument document)
    {
        if (document.InstanceId.ToString("D") != _plan.InstanceId || document.Root.GetProperty("operationId").GetString() != _plan.OperationId ||
            document.JournalId != Id || document.Kind.ToString() != "AssetPrepare" || document.PlanPath != reference.RelativePath ||
            document.PlanDigest != reference.Sha256 || !document.Root.GetProperty("resourceKeys").EnumerateArray().Select(value => value.GetString()).SequenceEqual(_plan.ResourceKeys))
            throw new IOException("asset_journal_plan_unbound");
        var receipts = document.Root.GetProperty("receipts");
        if (receipts.GetArrayLength() > 1 || receipts.EnumerateArray().Any(value => value.GetProperty("stepId").GetString() != "verify-no-effect" ||
            value.GetProperty("effect").GetString() != "notExecuted" || value.GetProperty("detailCode").GetString() != "asset_preparation_not_entered"))
            throw new IOException("asset_journal_receipts_unbound");
        _ = Workspace(document);
    }

    internal static bool HasNoEffects(JournalDocument? document) => document is not null && document.Root.GetProperty("receipts").GetArrayLength() == 1;
    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
}
