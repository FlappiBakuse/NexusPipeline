using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

internal sealed class AssetRecoverySession : IAssetRecoverySession, IDisposable
{
    private readonly StateDatabase _database;
    private readonly RuntimeInstance _instance;
    private readonly FrozenAssetPreparation _frozen;
    private readonly BlobReference _reference;
    private readonly AssetPreparationJournal _journal;
    private readonly AssetPreparationInspection _inspection;
    private readonly AssetPreparationPublication _publication;
    private readonly Func<Guid, WriterStopObservation> _writers;
    private readonly List<IDisposable> _held = [];

    internal AssetRecoverySession(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
        RegisteredJournal expected, Func<Guid, WriterStopObservation> writers)
    {
        _database = database;
        _instance = instance;
        _writers = writers;
        try
        {
            var blobs = new ImmutableBlobStore(instance, contracts);
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, expected.PlanPath));
            _reference = new(expected.PlanPath, new FileInfo(path).Length, expected.PlanDigest);
            _held.Add(blobs.OpenVerified(_reference));
            _frozen = new AssetPlanCodec(instance, contracts, blobs).Read(_reference);
            var plan = _frozen.ReadWire();
            _held.Add(blobs.OpenVerified(AssetPlanCodec.Reference(plan.Input)));
            if (expected.Kind.ToString() != "AssetPrepare" || expected.JournalId.ToString("D") != plan.JournalId ||
                expected.OperationId.ToString("D") != plan.OperationId || !expected.ResourceKeys.SequenceEqual(plan.ResourceKeys))
                throw new IOException("asset_recovery_registration_unbound");
            _journal = new(instance, contracts, database, _frozen, _reference);
            _publication = new(instance, contracts, database);
            var actual = _journal.Registered();
            if (actual.Revision != expected.Revision || actual.Phase != expected.Phase || actual.PlanDigest != expected.PlanDigest ||
                actual.OperationId != expected.OperationId || actual.Kind != expected.Kind || actual.PlanPath != expected.PlanPath ||
                !actual.ResourceKeys.SequenceEqual(expected.ResourceKeys))
                throw new IOException("asset_recovery_registration_changed");
            var physical = _journal.ReadOptional();
            if (physical is not null && physical.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Committed or JournalPhase.Quarantined))
                throw new IOException("asset_recovery_phase_invalid");
            var accepted = ReadResult();
            var closed = accepted.State is AutomationImportState.Published or AutomationImportState.Cancelled;
            if (accepted.JournalId != expected.JournalId || closed && (physical?.Phase != JournalPhase.Committed ||
                expected.Phase != JournalPhase.Committed || physical.Revision != expected.Revision ||
                (accepted.State == AutomationImportState.Cancelled) != AssetPreparationJournal.HasNoEffects(physical)))
                throw new IOException("asset_recovery_result_unbound");
            _inspection = new(instance, contracts, database, _frozen, _reference, _journal.Workspace(physical), !closed);
            _held.Add(_inspection);
            VerifyEffects(physical);
            if (!closed && physical?.Phase == JournalPhase.Committed &&
                !(AssetPreparationJournal.HasNoEffects(physical) ? _inspection.NoEffects : _inspection.AllContentReady))
                throw new IOException("asset_recorded_commit_unverified");
            if (!closed && _inspection.AllContentReady && physical?.Phase is JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Committed)
                _publication.VerifyDefinitions(_frozen);
        }
        catch { Dispose(); throw; }
    }

    internal JournalRecoveryResult Recover()
    {
        if (_journal.ReadOptional()?.Phase == JournalPhase.Quarantined)
        {
            _journal.ProjectNonterminal();
            MarkQuarantined("asset_preparation_isolated");
        }
        return new AutomationAssetRecovery(this).Recover();
    }

    public AssetRecoveryInspection Inspect()
    {
        var physical = _journal.ReadOptional();
        var phase = physical?.Phase ?? JournalPhase.Prepared;
        var registered = _journal.Registered();
        var result = ReadResult();
        var observation = _writers(_journal.Id);
        var gate = new Sqlite.JournalEffectGate(_database).Read(_journal.Id);
        var agrees = physical is not null && registered.Revision == physical.Revision && registered.Phase == phase &&
            result.State is AutomationImportState.Published or AutomationImportState.Cancelled;
        var noEffects = StillNoEffects() && (phase == JournalPhase.Prepared || AssetPreparationJournal.HasNoEffects(physical));
        return new(phase, agrees, observation.IsComplete, observation.State, gate.State, noEffects,
            _inspection.AllContentReady, noEffects || _inspection.AllContentReady);
    }

    public void SealEffectEntry()
    {
        var gate = new Sqlite.JournalEffectGate(_database);
        gate.SealUnentered(_journal.Registered(), gate.Read(_journal.Id).Revision);
    }

    public void CommitNoEffects()
    {
        if (!StillNoEffects() || new Sqlite.JournalEffectGate(_database).Read(_journal.Id).State != EffectGateState.Sealed)
            throw new IOException("asset_no_effect_evidence_changed");
        var document = _journal.Append(JournalPhase.Committed, noEffects: true);
        CloseNoEffects(document);
    }

    public void PublishCompleteDefinitions()
    {
        if (!_inspection.AllContentReady) throw new IOException("asset_publication_content_incomplete");
        SynchronizeEffects();
        _journal.ProjectNonterminal();
        var physical = _journal.ReadOptional()!;
        if (physical.Phase == JournalPhase.Applying)
        {
            _journal.Append(JournalPhase.Applied);
            _journal.ProjectNonterminal();
        }
        var committed = _journal.Append(JournalPhase.Committed);
        _publication.Close(_frozen, _reference, committed, false);
    }

    public void ReconcileRecordedCommit()
    {
        var document = _journal.ReadOptional()!;
        if (AssetPreparationJournal.HasNoEffects(document)) { CloseNoEffects(document); return; }
        if (!_inspection.AllContentReady) throw new IOException("asset_recorded_content_unverified");
        SynchronizeEffects();
        _journal.ProjectNonterminal();
        _publication.Close(_frozen, _reference, document, false);
    }

    public void Quarantine(string reasonCode)
    {
        _journal.Append(JournalPhase.Quarantined);
        _journal.ProjectNonterminal();
        MarkQuarantined(reasonCode);
    }

    private void CloseNoEffects(JournalDocument document)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(document.Export()));
        var outbox = new FileEffectOutbox(_database);
        foreach (var effect in outbox.Read(_journal.Id).Where(value => value.State == FileEffectState.Pending))
            outbox.Record(_journal.Id, effect.StepId, FileEffectState.Pending, FileEffectState.NotExecuted, digest, new(true, false, false, false));
        _publication.Close(_frozen, _reference, document, true);
    }

    private void SynchronizeEffects()
    {
        var outbox = new FileEffectOutbox(_database);
        foreach (var effect in outbox.Read(_journal.Id))
        {
            var asset = _frozen.ReadWire().Assets.First(value => AssetPreparationAcceptance.Step(value.Content.Sha256) == effect.StepId);
            var receipts = _inspection.Evidence?.Receipts ?? throw new IOException("asset_publication_receipts_missing");
            var started = receipts[AssetEvidenceStore.FileName(asset, "started")];
            var completed = receipts[AssetEvidenceStore.FileName(asset, "completed")];
            var state = effect.State;
            if (state == FileEffectState.Pending)
            {
                outbox.Record(_journal.Id, effect.StepId, state, FileEffectState.Started, started.Sha256);
                state = FileEffectState.Started;
            }
            if (state == FileEffectState.Started) outbox.Record(_journal.Id, effect.StepId, state, FileEffectState.Completed, completed.Sha256);
        }
    }

    private void VerifyEffects(JournalDocument? physical)
    {
        var expected = _frozen.ReadWire().Assets.Where(value => !value.WasPresent).DistinctBy(value => value.Content.Sha256).ToArray();
        var effects = new FileEffectOutbox(_database).Read(_journal.Id);
        if (effects.Count != expected.Length) throw new IOException("asset_outbox_inventory_unbound");
        foreach (var effect in effects)
        {
            var asset = expected.SingleOrDefault(value => AssetPreparationAcceptance.Step(value.Content.Sha256) == effect.StepId)
                ?? throw new IOException("asset_outbox_step_unbound");
            if (effect.Action != FileEffectAction.InstallCandidate || effect.IntentDigest != _reference.Sha256)
                throw new IOException("asset_outbox_intent_unbound");
            var name = effect.State == FileEffectState.Completed ? "completed" : "started";
            if (effect.State is FileEffectState.Started or FileEffectState.Completed &&
                (_inspection.Evidence is null || !_inspection.Evidence.Receipts.TryGetValue(AssetEvidenceStore.FileName(asset, name), out var receipt) ||
                 receipt.Sha256 != effect.ReceiptDigest)) throw new IOException("asset_outbox_receipt_unbound");
            if (effect.State == FileEffectState.NotExecuted && (!AssetPreparationJournal.HasNoEffects(physical) ||
                effect.ReceiptDigest != Convert.ToHexStringLower(SHA256.HashData(physical!.Export()))))
                throw new IOException("asset_outbox_no_effect_unbound");
            if (effect.State == FileEffectState.Quarantined || physical is null && effect.State != FileEffectState.Pending)
                throw new IOException("asset_outbox_state_unrecognized");
        }
    }

    private AutomationImportResult ReadResult()
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return AssetPreparationAcceptance.ReadAccepted(connection, transaction, Guid.Parse(_frozen.ReadWire().OperationId), false).Result;
    }

    private bool StillNoEffects()
    {
        if (!_inspection.NoEffects) return false;
        var plan = _frozen.ReadWire();
        var paths = plan.Assets.Where(value => !value.WasPresent).Select(value => value.Content.RelativePath)
            .Append(AssetEvidenceCodec.WorkspacePath(plan));
        return paths.All(relative =>
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, relative));
            return !File.Exists(path) && !Directory.Exists(path);
        });
    }

    private void MarkQuarantined(string reason) => _database.Write((connection, transaction) =>
    {
        AutomationRowWriter.Row(connection, transaction, "UPDATE automation_imports SET state=3,reason_code=$0 WHERE journal_id=$1 AND state IN (0,3);", reason, _journal.Id);
        return true;
    });

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
}
