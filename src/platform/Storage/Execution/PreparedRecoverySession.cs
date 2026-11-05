using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed record AttemptWriterObservation(Guid InstanceId, Guid JournalId, bool IsComplete, WriterState State);

public sealed class PreparedRecoverySession : IPreparedRecoverySession, IPreparedConfigurationCompletion
{
    private readonly RuntimeInstance _instance;
    private readonly ContractReader _contracts;
    private readonly StateDatabase _database;
    private readonly RunAttemptPlan _plan;
    private readonly BlobReference _reference;
    private readonly JournalStore _store;
    private readonly RecoveryJournalRegistry _registry;
    private readonly JournalEffectGate _gate;
    private readonly AttemptPreparedProjection _projection;
    private readonly Func<AttemptWriterObservation> _writers;
    private readonly IAttemptPersistenceObserver? _observer;
    private readonly JsonObject _document;
    private readonly Guid _journalId;

    public PreparedRecoverySession(RuntimeInstance instance, Guid journalId, ContractReader contracts, StateDatabase database,
        Func<AttemptWriterObservation> writers, IAttemptPersistenceObserver? observer = null)
    {
        if (database.InstanceId != instance.InstanceId) throw new IOException("attempt_database_instance_unbound");
        _instance = instance; _contracts = contracts; _database = database; _writers = writers; _observer = observer; _journalId = journalId;
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, journalId);
        _plan = bound.Plan; _document = bound.Document;
        _store = new(instance, contracts); _registry = new(database); _gate = new(database);
        _projection = new(_store, _registry, new(database), observer);
        _reference = AttemptPreparedBinding.Reference(instance, _registry.ReadJournal(journalId));
    }

    public PreparedAttemptInspection Inspect()
    {
        var registered = _registry.ReadJournal(_journalId);
        var agrees = registered.Phase == Phase && registered.Revision == Revision;
        var gate = _gate.Read(_journalId);
        if (Phase == JournalPhase.Committed && agrees) return new(Phase, true, gate.State, false, WriterState.Unknown, false, false);
        var observed = Observe();
        if (!agrees && Phase == JournalPhase.Prepared && registered.Phase == JournalPhase.Prepared &&
            observed is { IsComplete: true, State: WriterState.Stopped } && gate.State == EffectGateState.Sealed)
        {
            ReconcilePreparedPrefix(registered);
            registered = _registry.ReadJournal(_journalId);
            agrees = registered.Phase == Phase && registered.Revision == Revision;
        }
        var noEffects = false;
        var pending = false;
        if (observed is { IsComplete: true, State: WriterState.Stopped } && gate.State != EffectGateState.Entered)
        {
            try { using var held = HoldNoEffects(); noEffects = true; }
            catch (IOException) { }
            if (Phase == JournalPhase.Prepared && agrees && gate.State == EffectGateState.Sealed)
            {
                try { _ = ConfigurationCompletionBinding.Read(_instance, _database, _contracts, _journalId); pending = true; }
                catch (IOException) { }
            }
        }
        return new(Phase, agrees, gate.State, observed.IsComplete, observed.State, noEffects,
            Phase == JournalPhase.Committed && noEffects && RecordedNoEffect()) { CanDeferConfigurationCompletion = pending };
    }

    public void VerifyPendingCompletion()
    {
        RequireStopped();
        _ = ConfigurationCompletionBinding.Read(_instance, _database, _contracts, _journalId);
        RequireStopped();
    }

    private void ReconcilePreparedPrefix(RegisteredJournal registered)
    {
        var bound = AttemptPreparedBinding.Read(_instance, _contracts, _database, _journalId);
        if (!JsonNode.DeepEquals(bound.Document, _document)) throw new IOException("attempt_configuration_parent_changed");
        var tail = _store.ReadAllRevisions(_journalId).Where(item => item.Revision > registered.Revision).ToArray();
        if (tail.Length == 0 || tail.Any(item => item.Phase != JournalPhase.Prepared ||
            SnapshotIntentCodec.Read(item.Root).Count == 0 || NoEffectReceipt.Recorded(item.Root))) return;
        RequireStopped();
        _projection.Synchronize(_plan, false);
    }

    public void SealEffectEntry()
    {
        RequireStopped();
        var registered = _registry.ReadJournal(_journalId);
        var gate = _gate.Read(_journalId);
        if (gate.State == EffectGateState.Sealed) return;
        _gate.SealUnentered(registered, gate.Revision);
        _observer?.Reached(AttemptPersistencePoint.GateSealed, _journalId, Revision, Phase);
    }

    public void CommitNoEffects()
    {
        RequireStopped();
        if (Phase != JournalPhase.Prepared || _gate.Read(_journalId).State != EffectGateState.Sealed) throw new IOException("attempt_no_effect_gate_unverified");
        RequireNoCancellation();
        using var proof = HoldNoEffects();
        _projection.Synchronize(_plan, true);
        if (!RecordedNoEffect()) Save(JournalPhase.Prepared, "verify-no-effect", "notExecuted", NoEffectReceipt.Reason, true);
        RequireStopped();
        Save(JournalPhase.Committed, "verify-no-effect", "notExecuted", NoEffectReceipt.Reason, true);
    }

    public void Quarantine(string reasonCode)
    {
        _projection.Synchronize(_plan, false);
        Save(JournalPhase.Quarantined, "isolate-attempt", "unknown", reasonCode, false);
    }

    public void ReconcileRecordedCommit()
    {
        RequireStopped();
        RequireNoCancellation();
        if (Phase != JournalPhase.Committed || _gate.Read(_journalId).State != EffectGateState.Sealed || !RecordedNoEffect())
            throw new IOException("attempt_recorded_commit_unverified");
        using var proof = HoldNoEffects();
        _projection.Synchronize(_plan, true);
    }

    private NoEffectLease HoldNoEffects() => NoEffectLease.Acquire(_instance, _contracts, _database, _plan, _reference);
    private void RequireNoCancellation()
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, _journalId);
        transaction.Commit();
    }
    private bool RecordedNoEffect() => NoEffectReceipt.Recorded(JsonSerializer.SerializeToElement(_document));
    private AttemptWriterObservation Observe()
    {
        var observed = _writers();
        return observed.InstanceId == _instance.InstanceId && observed.JournalId == _journalId ? observed : new(_instance.InstanceId, _journalId, false, WriterState.Unknown);
    }

    private void RequireStopped()
    {
        if (Observe() is not { IsComplete: true, State: WriterState.Stopped }) throw new IOException("attempt_writer_stop_unconfirmed");
    }

    private void Save(JournalPhase phase, string step, string effect, string detail, bool verifiedNoEffects)
    {
        var previous = Revision;
        _document["revision"] = previous + 1; _document["phase"] = phase.ToString().ToLowerInvariant();
        _document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        var receipt = new JsonObject
        {
            ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = _document["updatedAt"]!.GetValue<string>(),
            ["stepId"] = step, ["effect"] = effect, ["detailCode"] = detail
        };
        if (verifiedNoEffects) receipt["outcomes"] = JsonSerializer.SerializeToNode(NoEffectReceipt.Outcomes());
        _document["receipts"]!.AsArray().Add(receipt);
        var next = _store.Append(_journalId, previous, JsonSerializer.SerializeToUtf8Bytes(_document),
            phase == JournalPhase.Committed && verifiedNoEffects ? new(true, false, false, false) : null);
        _observer?.Reached(AttemptPersistencePoint.JournalFlushed, _journalId, next.Revision, next.Phase);
        _projection.Synchronize(_plan, verifiedNoEffects);
    }

    private long Revision => _document["revision"]!.GetValue<long>();
    private JournalPhase Phase => Enum.Parse<JournalPhase>(_document["phase"]!.GetValue<string>(), true);
}
