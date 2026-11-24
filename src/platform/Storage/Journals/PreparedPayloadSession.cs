using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed record PayloadWriterObservation(Guid InstanceId, Guid JournalId, bool IsComplete, WriterState State);

public sealed class PreparedPayloadSession : IPayloadRecoverySession
{
    private readonly RuntimeInstance _instance;
    private readonly FrozenPayloadApply _plan;
    private readonly BlobReference _reference;
    private readonly ImmutableBlobStore _blobs;
    private readonly JournalStore _store;
    private readonly RecoveryJournalRegistry _registry;
    private readonly JournalEffectGate _gate;
    private readonly FileEffectOutbox _outbox;
    private readonly PayloadPreparedProjection _projection;
    private readonly Func<PayloadWriterObservation> _writers;
    private readonly IPayloadPersistenceObserver? _observer;
    private readonly JsonObject _document;
    private (Type Type, int HResult)? _lastProofFailure;

    public PreparedPayloadSession(RuntimeInstance instance, Guid journalId, ContractReader contracts, StateDatabase database,
        PayloadApplyRoots roots, Func<PayloadWriterObservation> writers, IPayloadPersistenceObserver? observer = null)
    {
        if (database.InstanceId != instance.InstanceId) throw new IOException("payload_database_instance_unbound");
        _instance = instance; _writers = writers; _observer = observer;
        var bound = PayloadPreparedBinding.Read(instance, contracts, database, roots, journalId);
        _plan = bound.Plan; _reference = bound.Reference; _document = bound.Document;
        _store = new(instance, contracts); _registry = new(database); _gate = new(database); _outbox = new(database);
        _projection = new(_store, _registry, _outbox, observer); _blobs = new(instance, contracts);
    }

    public PreparedPayloadInspection Inspect()
    {
        var registered = _registry.ReadJournal(_plan.JournalId);
        PayloadPreparedBinding.RequireRegistered(_plan, _reference, registered);
        var agrees = registered.Phase == Phase && registered.Revision == Revision;
        var gate = ReadGate();
        if (Phase == JournalPhase.Committed && agrees) return new(Phase, true, gate.State, false, WriterState.Unknown, false, false);
        var observed = Observe();
        var verified = false;
        if (observed is { IsComplete: true, State: WriterState.Stopped } && gate.State != EffectGateState.Entered)
        {
            try
            {
                using (HoldNoEffects()) { }
                verified = true; _lastProofFailure = null;
            }
            catch (IOException error)
            {
                var failure = (error.GetType(), error.HResult);
                if (_lastProofFailure != failure) ExecutionFailureTrace.Record(_instance.InstanceId, _plan.OperationId, "payload-no-effect-proof", error);
                _lastProofFailure = failure;
            }
        }
        var terminalOutbox = _outbox.Read(_plan.JournalId).All(item => item.State is FileEffectState.Pending or FileEffectState.NotExecuted);
        var noEffects = verified && terminalOutbox;
        return new(Phase, agrees, gate.State, observed.IsComplete, observed.State, noEffects,
            Phase == JournalPhase.Committed && noEffects && RecordedNoEffect());
    }

    public void SealEffectEntry()
    {
        RequireStopped();
        var registered = _registry.ReadJournal(_plan.JournalId);
        PayloadPreparedBinding.RequireRegistered(_plan, _reference, registered);
        var gate = ReadGate();
        if (gate.State == EffectGateState.Sealed) return;
        _gate.SealUnentered(registered, gate.Revision);
        _observer?.Reached(PayloadPersistencePoint.GateSealed, _plan.JournalId, Revision, Phase);
    }

    public void CommitNoEffects()
    {
        RequireStopped();
        if (Phase != JournalPhase.Prepared || ReadGate().State != EffectGateState.Sealed)
            throw new IOException("payload_no_effect_gate_unverified");
        using var proof = HoldNoEffects();
        _projection.Synchronize(_plan, _reference, verifiedNoEffects: true);
        if (!RecordedNoEffect()) Save(JournalPhase.Prepared, "verify-no-effect", "notExecuted", "payload_not_executed", true);
        RequireStopped();
        Save(JournalPhase.Committed, "verify-no-effect", "notExecuted", "payload_not_executed", true);
    }

    public void Quarantine(string reasonCode)
    {
        _projection.Synchronize(_plan, _reference, verifiedNoEffects: false);
        Save(JournalPhase.Quarantined, "isolate-payload", "unknown", reasonCode, false);
    }

    public void ReconcileRecordedCommit()
    {
        RequireStopped();
        if (Phase != JournalPhase.Committed || ReadGate().State != EffectGateState.Sealed || !RecordedNoEffect())
            throw new IOException("payload_recorded_commit_unverified");
        using var proof = HoldNoEffects();
        _projection.Synchronize(_plan, _reference, verifiedNoEffects: true);
    }

    private EffectGateSnapshot ReadGate() => PayloadPreparedBinding.RequireGate(_plan, _reference, _gate.Read(_plan.JournalId));
    private UnenteredPayloadLease HoldNoEffects() => UnenteredPayloadLease.Acquire(_instance, _plan, _blobs, _reference);
    private bool RecordedNoEffect() => _document["receipts"]!.AsArray().Any(item =>
        PayloadPreparedBinding.IsUnexecutedReceipt(item!["stepId"]!.GetValue<string>(), item["effect"]!.GetValue<string>()));
    private PayloadWriterObservation Observe()
    {
        var observed = _writers();
        return observed.InstanceId == _plan.InstanceId && observed.JournalId == _plan.JournalId ? observed :
            new(_plan.InstanceId, _plan.JournalId, false, WriterState.Unknown);
    }

    private void RequireStopped()
    {
        if (Observe() is not { IsComplete: true, State: WriterState.Stopped }) throw new IOException("payload_writer_stop_unconfirmed");
    }

    private void Save(JournalPhase phase, string step, string effect, string detail, bool verifiedNoEffects)
    {
        var previous = Revision;
        _document["revision"] = previous + 1; _document["phase"] = phase.ToString().ToLowerInvariant();
        _document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        _document["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = _document["updatedAt"]!.GetValue<string>(),
            ["stepId"] = step, ["effect"] = effect, ["detailCode"] = detail
        });
        var noEffect = phase == JournalPhase.Committed && verifiedNoEffects ? new NoEffectEvidence(true, false, false, false) : null;
        var next = _store.Append(_plan.JournalId, previous, JsonSerializer.SerializeToUtf8Bytes(_document), noEffect);
        _observer?.Reached(PayloadPersistencePoint.JournalFlushed, _plan.JournalId, next.Revision, next.Phase);
        _projection.Synchronize(_plan, _reference, verifiedNoEffects);
    }

    private long Revision => _document["revision"]!.GetValue<long>();
    private JournalPhase Phase => Enum.Parse<JournalPhase>(_document["phase"]!.GetValue<string>(), true);
}
