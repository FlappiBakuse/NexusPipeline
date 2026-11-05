using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class PreparationRecoverySession : IDisposable
{
    private readonly StateDatabase _database;
    private readonly FrozenSecretPreparation _frozen;
    private readonly BlobReference _reference;
    private readonly SecretPreparationJournal _journal;
    private readonly SecretPreparationEvidence _evidence;
    private readonly SecretPreparationPublication _publication;
    private readonly PreparationEvidenceStore _store;
    private readonly Func<Guid, WriterStopObservation> _writers;
    private readonly List<IDisposable> _held = [];

    internal PreparationRecoverySession(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
        RegisteredJournal expected, Func<Guid, WriterStopObservation> writers)
    {
        _database = database; _writers = writers; _publication = new(instance, database); _store = new(instance, contracts, database);
        try
        {
            var blobs = new ImmutableBlobStore(instance, contracts);
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, expected.PlanPath));
            _reference = new(expected.PlanPath, new FileInfo(path).Length, expected.PlanDigest); _held.Add(blobs.OpenVerified(_reference));
            _frozen = new PreparationPlanCodec(instance, contracts, database).Read(_reference);
            var plan = _frozen.ReadWire();
            if (expected.Kind != JournalKind.SecretPrepare || expected.JournalId.ToString("D") != plan.JournalId ||
                expected.OperationId.ToString("D") != plan.OperationId || !expected.ResourceKeys.SequenceEqual(plan.ResourceKeys))
                throw new IOException("secret_recovery_registration_unbound");
            if (plan.Retirement.Plan is { } retirement) _held.Add(blobs.OpenVerified(new(retirement.RelativePath, retirement.SizeBytes, retirement.Sha256)));
            _journal = new(instance, contracts, database, _frozen, _reference);
            if (_journal.Registered() != expected && !SameRegistration(_journal.Registered(), expected)) throw new IOException("secret_recovery_registration_changed");
            var physical = _journal.ReadOptional();
            var state = State(); var closed = state is SecretPreparationState.Published or SecretPreparationState.Cancelled;
            if (!closed)
            {
                using var keyConnection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
                _held.Add(SecretReplayProof.Hold(instance, keyConnection));
            }
            VerifySql(closed, state, physical);
            PreparationEvidenceStore.VerifyInventory(instance, database);
            _evidence = new PreparationEvidenceStore(instance, contracts, database).Read(_frozen, _reference, inspectCandidate: !closed);
            _held.Add(_evidence);
            VerifyEffects(physical);
            _ = Decision();
        }
        catch { Dispose(); throw; }
    }

    internal JournalRecoveryResult Recover()
    {
        if (_journal.ReadOptional()?.Phase == JournalPhase.Quarantined && State() == SecretPreparationState.Quarantined)
            return JournalRecoveryResult.Quarantined;
        var action = Decision();
        if (action == PreparationRecoveryAction.AlreadyCompleted) return JournalRecoveryResult.AlreadyCompleted;
        if (action == PreparationRecoveryAction.Quarantine)
        {
            var current = _journal.Materialize();
            if (current.Phase != JournalPhase.Quarantined) _journal.Append(JournalPhase.Quarantined);
            _journal.ProjectNonterminal(); SetQuarantined();
            return JournalRecoveryResult.Quarantined;
        }
        _journal.Materialize(); _journal.ProjectNonterminal();
        if (action == PreparationRecoveryAction.CancelNoEffects)
        {
            var gate = new JournalEffectGate(_database).Read(_journal.Id);
            new JournalEffectGate(_database).SealUnentered(_journal.Registered(), gate.Revision);
            var terminal = _journal.ReadOptional()!;
            if (terminal.Phase != JournalPhase.Committed) terminal = _journal.Append(JournalPhase.Committed, noEffects: true);
            CompleteOutbox(terminal, cancelled: true);
            _publication.Close(_frozen, _reference, terminal, cancelled: true);
            return JournalRecoveryResult.NotExecuted;
        }
        CompleteOutbox(_journal.ReadOptional()!, cancelled: false);
        var document = _journal.ReadOptional()!;
        if (document.Phase == JournalPhase.Applying) { _journal.Append(JournalPhase.Applied); _journal.ProjectNonterminal(); }
        document = _journal.ReadOptional()!;
        if (document.Phase != JournalPhase.Committed) document = _journal.Append(JournalPhase.Committed);
        _publication.Close(_frozen, _reference, document, cancelled: false);
        return JournalRecoveryResult.Completed;
    }

    private PreparationRecoveryAction Decision()
    {
        var plan = _frozen.ReadWire(); var physical = _journal.ReadOptional(); var registered = _journal.Registered(); var state = State();
        var closed = state is SecretPreparationState.Published or SecretPreparationState.Cancelled;
        if (closed) return PreparationRecoveryAction.AlreadyCompleted;
        var observation = _writers(_journal.Id); var gate = new JournalEffectGate(_database).Read(_journal.Id);
        var beforeMatches = BeforeMatches();
        var noEffects = _evidence.NoEffects && _store.StillNoEffects(plan) && (physical?.Phase is null or JournalPhase.Prepared || SecretPreparationJournal.NoEffects(physical));
        if (physical?.Phase == JournalPhase.Committed && SecretPreparationJournal.NoEffects(physical))
        {
            if (!noEffects || !beforeMatches || gate.State != EffectGateState.Sealed || !observation.IsComplete || observation.State != Core.Execution.WriterState.Stopped)
                throw new IOException("secret_cancelled_commit_unverified");
            return PreparationRecoveryAction.CancelNoEffects;
        }
        return SecretPreparationRecovery.Decide(new(physical?.Phase ?? JournalPhase.Prepared,
            physical is not null && physical.Revision == registered.Revision && physical.Phase == registered.Phase,
            observation.IsComplete, observation.State, gate.State == EffectGateState.Entered, noEffects, beforeMatches,
            _evidence.CandidateVerified, _evidence.Completed is not null, false));
    }

    private void VerifySql(bool closed, SecretPreparationState state, JournalDocument? physical)
    {
        var plan = _frozen.ReadWire();
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        SecretPreparationRows.VerifyPlan(connection, transaction, plan);
        var identity = SecretFieldStore.Identity(Guid.Parse(plan.OperationId), SecretPreparationRows.Key(plan), plan.ExpectedRevision, SecretDisposition.Set);
        var operation = OperationRows.ReadExisting(connection, transaction, identity) ?? throw new IOException("secret_recovery_operation_missing");
        if (closed)
        {
            if (operation.Status != "completed" || physical?.Phase != JournalPhase.Committed || physical.Revision != _journal.Registered().Revision ||
                _journal.Registered().Phase != JournalPhase.Committed || (state == SecretPreparationState.Cancelled) != SecretPreparationJournal.NoEffects(physical))
                throw new IOException("secret_recovery_closed_result_unbound");
            if (state == SecretPreparationState.Published)
            {
                var result = SecretFieldStore.ReadReplay(connection, transaction, identity, plan.ReplayProof);
                if (result is null || result.Revision != plan.ExpectedRevision + 1 || result.SecretId?.ToString("D") != plan.Candidate.SecretId || !result.IsReadable)
                    throw new IOException("secret_recovery_original_result_unbound");
            }
        }
        else if (operation.Status != "accepted") throw new IOException("secret_recovery_pending_result_unbound");
    }

    private void VerifyEffects(JournalDocument? physical)
    {
        var effect = Effect();
        if (effect.StepId != "secret-payload" || effect.Action != FileEffectAction.InstallCandidate || effect.IntentDigest != _reference.Sha256)
            throw new IOException("secret_recovery_effect_unbound");
        var expected = effect.State switch
        {
            FileEffectState.Pending => null,
            FileEffectState.Started => _evidence.Started?.Digest ?? throw new IOException("secret_started_receipt_missing"),
            FileEffectState.Completed => _evidence.Completed?.Digest ?? throw new IOException("secret_completed_receipt_missing"),
            FileEffectState.NotExecuted when SecretPreparationJournal.NoEffects(physical) => Convert.ToHexStringLower(SHA256.HashData(physical!.Export())),
            _ => throw new IOException("secret_recovery_effect_state_unrecognized")
        };
        if (effect.ReceiptDigest != expected) throw new IOException("secret_recovery_effect_receipt_unbound");
    }

    private void CompleteOutbox(JournalDocument terminal, bool cancelled)
    {
        var effect = Effect(); var outbox = new FileEffectOutbox(_database);
        if (cancelled)
        {
            if (effect.State == FileEffectState.Pending) outbox.Record(_journal.Id, effect.StepId, effect.State, FileEffectState.NotExecuted,
                Convert.ToHexStringLower(SHA256.HashData(terminal.Export())), new(true, false, false, false));
            return;
        }
        if (effect.State == FileEffectState.Pending)
            outbox.Record(_journal.Id, effect.StepId, effect.State, FileEffectState.Started, _evidence.Started!.Digest);
        if (Effect().State == FileEffectState.Started)
            outbox.Record(_journal.Id, effect.StepId, FileEffectState.Started, FileEffectState.Completed, _evidence.Completed!.Digest);
    }

    private bool BeforeMatches()
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        try { SecretPreparationRows.VerifyBefore(connection, transaction, _frozen.ReadWire()); return true; }
        catch (InvalidOperationException) { return false; }
    }
    private SecretPreparationState State()
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        return SecretPreparationRows.Read(connection, null, Guid.Parse(_frozen.ReadWire().OperationId), _frozen.ReadWire().ReplayProof);
    }
    private FileEffect Effect() => new FileEffectOutbox(_database).Read(_journal.Id).Single();
    private void SetQuarantined() => _database.Write((connection, transaction) =>
        { SecretPreparationRows.SetState(connection, transaction, _frozen.ReadWire(), SecretPreparationState.Quarantined); return true; });
    private static bool SameRegistration(RegisteredJournal a, RegisteredJournal b) => a.JournalId == b.JournalId && a.OperationId == b.OperationId &&
        a.Kind == b.Kind && a.Phase == b.Phase && a.Revision == b.Revision && a.PlanPath == b.PlanPath && a.PlanDigest == b.PlanDigest && a.ResourceKeys.SequenceEqual(b.ResourceKeys);
    public void Dispose() { for (var i = _held.Count - 1; i >= 0; i--) _held[i].Dispose(); }
}
