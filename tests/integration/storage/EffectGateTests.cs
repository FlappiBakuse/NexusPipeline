using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using PersistentEffectGate = NexusPipeline.Platform.Storage.Sqlite.JournalEffectGate;

namespace NexusPipeline.StorageTests;

public sealed class EffectGateTests
{
    [Theory(DisplayName = "Prepared Effect Gate Is Accepted With Its Journal And Sealing Permanently Excludes Effect Entry")]
    [InlineData(JournalKind.RunAttempt)]
    [InlineData(JournalKind.UpdateApply)]
    [InlineData(JournalKind.ExtensionApply)]
    public void SealsAcceptedGate(JournalKind kind)
    {
        var (database, journal, _) = Create(kind);
        var gate = new PersistentEffectGate(database);
        var initial = gate.Read(journal.JournalId);
        AssertGate(initial, database, journal, EffectGateState.Unentered);
        var sealedGate = gate.SealUnentered(journal, initial.Revision);
        Assert.Equal(EffectGateState.Sealed, sealedGate.State);
        Assert.Equal(sealedGate, gate.SealUnentered(journal, sealedGate.Revision));
        Assert.Equal("effect_gate_already_decided", Assert.Throws<IOException>(() => gate.Enter(journal, sealedGate.Revision)).Message);
        Assert.Equal("effect_gate_revision_conflict", Assert.Throws<IOException>(() => gate.Enter(journal, initial.Revision)).Message);
        Assert.Equal(sealedGate, new PersistentEffectGate(database).Read(journal.JournalId));
        AssertPending(database, journal);
    }

    [Theory(DisplayName = "Entered Gate Cannot Be Resealed Or Grant A Second Executor")]
    [InlineData(JournalKind.RunAttempt)]
    [InlineData(JournalKind.UpdateApply)]
    [InlineData(JournalKind.ExtensionApply)]
    public void RejectsRepeatedEntry(JournalKind kind)
    {
        var (database, journal, _) = Create(kind);
        var gate = new PersistentEffectGate(database);
        var initial = gate.Read(journal.JournalId);
        var entered = gate.Enter(journal, initial.Revision);
        AssertGate(entered, database, journal, EffectGateState.Entered);
        Assert.Equal("effect_gate_already_decided", Assert.Throws<IOException>(() => gate.SealUnentered(journal, entered.Revision)).Message);
        Assert.Equal("effect_gate_already_decided", Assert.Throws<IOException>(() => gate.Enter(journal, entered.Revision)).Message);
        Assert.Equal(entered, new PersistentEffectGate(database).Read(journal.JournalId));
        AssertPending(database, journal);
    }

    [Theory(DisplayName = "Concurrent Entry And Sealing Have Exactly One Durable Winner")]
    [InlineData(JournalKind.RunAttempt)]
    [InlineData(JournalKind.UpdateApply)]
    [InlineData(JournalKind.ExtensionApply)]
    public async Task SerializesGateDecision(JournalKind kind)
    {
        var (database, journal, _) = Create(kind);
        var initial = new PersistentEffectGate(database).Read(journal.JournalId);
        using var ready = new Barrier(2);
        var attempts = new[] { EffectGateState.Entered, EffectGateState.Sealed }.Select(next => Task.Run(() =>
        {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(10)));
            var gate = new PersistentEffectGate(database);
            try { return next == EffectGateState.Entered ? gate.Enter(journal, initial.Revision) : gate.SealUnentered(journal, initial.Revision); }
            catch (IOException error) when (error.Message == "effect_gate_revision_conflict") { return null; }
        })).ToArray();
        var winner = Assert.Single((await Task.WhenAll(attempts)).OfType<EffectGateSnapshot>());
        Assert.Equal(winner, new PersistentEffectGate(database).Read(journal.JournalId));
        Assert.Contains(winner.State, new[] { EffectGateState.Entered, EffectGateState.Sealed });
        AssertPending(database, journal);
    }

    [Theory(DisplayName = "Unbound Or Stale Journal Cannot Seal Or Enter Another Operation")]
    [InlineData("operation")]
    [InlineData("kind")]
    [InlineData("digest")]
    [InlineData("path")]
    [InlineData("resources")]
    [InlineData("revision")]
    [InlineData("phase")]
    public void RejectsUnboundJournals(string fault)
    {
        var (database, journal, _) = Create(JournalKind.RunAttempt);
        var altered = fault switch
        {
            "operation" => journal with { OperationId = Guid.NewGuid() },
            "kind" => journal with { Kind = JournalKind.ExtensionApply },
            "digest" => journal with { PlanDigest = new string('b', 64) },
            "path" => journal with { PlanPath = "blobs/plans/unknown.json" },
            "resources" => journal with { ResourceKeys = ["run:other"] },
            "revision" => journal with { Revision = 2 },
            "phase" => journal with { Phase = JournalPhase.Applying },
            _ => throw new InvalidOperationException()
        };
        var gate = new PersistentEffectGate(database);
        var before = gate.Read(journal.JournalId);
        Assert.Equal("effect_gate_journal_binding_changed", Assert.Throws<IOException>(() => gate.SealUnentered(altered, before.Revision)).Message);
        Assert.Equal("effect_gate_journal_binding_changed", Assert.Throws<IOException>(() => gate.Enter(altered, before.Revision)).Message);
        Assert.Equal(before, gate.Read(journal.JournalId));
        AssertPending(database, journal);
    }

    [Fact(DisplayName = "Repeated Acceptance Retains The Original Gate And Does Not Reopen It")]
    public void PreservesOriginalGate()
    {
        var (database, journal, operation) = Create(JournalKind.UpdateApply);
        var gate = new PersistentEffectGate(database);
        var sealedGate = gate.SealUnentered(journal, gate.Read(journal.JournalId).Revision);
        var registry = new RecoveryJournalRegistry(database);
        var substitute = journal with { JournalId = Guid.NewGuid() };
        var replay = registry.AcceptPrepared(operation, substitute, [], DateTimeOffset.UtcNow);
        Assert.False(replay.Acceptance.IsNew);
        Assert.Equal(operation.OperationId, replay.Acceptance.OperationId);
        Assert.Equal(journal.JournalId, replay.JournalId);
        Assert.Null(registry.FindJournal(substitute.JournalId));
        Assert.Equal(sealedGate, gate.Read(replay.JournalId));
        AssertPending(database, journal);
    }

    [Fact(DisplayName = "Failed Compound Acceptance Does Not Leave A Gate Or Accepted Operation")]
    public void RevertsFailedAcceptance()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture-gate-rollback", new string('a', 64), "fixture.effect-gate");
        var digest = new string('c', 64);
        var journal = new RegisteredJournal(Guid.NewGuid(), operation.OperationId, JournalKind.RunAttempt, JournalPhase.Prepared, 1,
            $"blobs/plans/{digest}.json", digest, ["fixture:rollback"]);
        var registry = new RecoveryJournalRegistry(database);
        Assert.Throws<ArgumentException>(() => registry.AcceptPrepared(operation, journal,
            [new(journal.JournalId, "invalid step", FileEffectAction.InstallCandidate, FileEffectState.Pending, digest, null)], DateTimeOffset.UtcNow));
        Assert.Null(registry.FindJournal(journal.JournalId));
        var gate = new PersistentEffectGate(database);
        Assert.Equal("effect_gate_missing", Assert.Throws<IOException>(() => gate.Read(journal.JournalId)).Message);
        Assert.Empty(new FileEffectOutbox(database).Read(journal.JournalId));
        var accepted = registry.AcceptPrepared(operation, journal, [], DateTimeOffset.UtcNow);
        Assert.True(accepted.Acceptance.IsNew);
        Assert.Equal(journal.JournalId, accepted.JournalId);
        AssertGate(gate.Read(journal.JournalId), database, journal, EffectGateState.Unentered);
    }

    [Fact(DisplayName = "Advancing The Journal Prevents Late Entry And Late No Effect Sealing")]
    public void RejectsLateEntry()
    {
        var (database, journal, _) = Create(JournalKind.ExtensionApply);
        var gate = new PersistentEffectGate(database);
        var entered = gate.Enter(journal, gate.Read(journal.JournalId).Revision);
        var applying = journal with { Phase = JournalPhase.Applying, Revision = journal.Revision + 1 };
        new RecoveryJournalRegistry(database).Advance(applying, journal.Revision);
        Assert.Equal("effect_gate_journal_binding_changed", Assert.Throws<IOException>(() => gate.Enter(journal, entered.Revision)).Message);
        Assert.Equal("effect_gate_journal_binding_changed", Assert.Throws<IOException>(() => gate.SealUnentered(applying, entered.Revision)).Message);
        Assert.Equal(entered, gate.Read(journal.JournalId));
        AssertPending(database, applying);
    }

    [Theory(DisplayName = "Journal Cannot Begin Applying Or Commit No Effects Without Its Durable Gate Decision")]
    [InlineData(JournalKind.RunAttempt)]
    [InlineData(JournalKind.UpdateApply)]
    [InlineData(JournalKind.ExtensionApply)]
    public void RequiresDurableDecision(JournalKind kind)
    {
        var (database, journal, _) = Create(kind);
        var registry = new RecoveryJournalRegistry(database);
        var applying = journal with { Phase = JournalPhase.Applying, Revision = 2 };
        var committed = journal with { Phase = JournalPhase.Committed, Revision = 2 };
        var evidence = new NoEffectEvidence(true, false, false, false);
        var outbox = new FileEffectOutbox(database);
        Assert.Empty(outbox.Read(journal.JournalId));
        Assert.Equal("effect_gate_decision_required", Assert.Throws<IOException>(() => registry.Advance(applying, 1)).Message);
        Assert.Equal("effect_gate_decision_required", Assert.Throws<IOException>(() => registry.Advance(committed, 1, evidence)).Message);
        AssertPending(database, journal);
        var gate = new PersistentEffectGate(database);
        gate.SealUnentered(journal, gate.Read(journal.JournalId).Revision);
        Assert.Equal("effect_gate_decision_required", Assert.Throws<IOException>(() => registry.Advance(applying, 1)).Message);
        registry.Advance(committed, 1, evidence);
        AssertRegistration(registry, committed);
        Assert.Empty(registry.ReadSnapshot().Isolations);
        Assert.Empty(outbox.Read(journal.JournalId));
    }

    [Fact(DisplayName = "A File Effect Cannot Start Before Both Entry And Applying Are Durable")]
    public void RequiresEnteredGate()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture-effect-start", new string('a', 64), "fixture.effect-gate");
        var digest = new string('c', 64);
        var journal = new RegisteredJournal(Guid.NewGuid(), operation.OperationId, JournalKind.UpdateApply, JournalPhase.Prepared, 1,
            $"blobs/plans/{digest}.json", digest, ["fixture:effect-start"]);
        var effect = new FileEffect(journal.JournalId, "install", FileEffectAction.InstallCandidate, FileEffectState.Pending, digest, null);
        var registry = new RecoveryJournalRegistry(database);
        registry.AcceptPrepared(operation, journal, [effect], DateTimeOffset.UtcNow);
        var outbox = new FileEffectOutbox(database);
        var receipt = new string('d', 64);
        void Start() => outbox.Record(journal.JournalId, effect.StepId, FileEffectState.Pending, FileEffectState.Started, receipt);
        Assert.Equal("effect_gate_decision_required", Assert.Throws<IOException>(Start).Message);
        Assert.Equal(effect, Assert.Single(outbox.Read(journal.JournalId)));
        var gate = new PersistentEffectGate(database);
        gate.Enter(journal, gate.Read(journal.JournalId).Revision);
        Assert.Equal("effect_gate_decision_required", Assert.Throws<IOException>(Start).Message);
        Assert.Equal(effect, Assert.Single(outbox.Read(journal.JournalId)));
        registry.Advance(journal with { Phase = JournalPhase.Applying, Revision = 2 }, 1);
        Start();
        Assert.Equal(effect with { State = FileEffectState.Started, ReceiptDigest = receipt }, Assert.Single(outbox.Read(journal.JournalId)));
    }

    [Theory(DisplayName = "Contradictory Gate State And Revision Cannot Authorize No Effect Evidence")]
    [InlineData("sealed", 1)]
    [InlineData("entered", 1)]
    [InlineData("unentered", 2)]
    public void RejectsContradictoryEvidence(string state, int revision)
    {
        var (database, journal, _) = Create(JournalKind.RunAttempt);
        using (var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={database.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE main.journal_effect_gates SET state=$state,revision=$revision WHERE journal_id=$id;";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$id", journal.JournalId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.Equal("effect_gate_state_revision_unbound", Assert.Throws<IOException>(() => new PersistentEffectGate(database).Read(journal.JournalId)).Message);
        var registry = new RecoveryJournalRegistry(database);
        Assert.Throws<IOException>(() => registry.Advance(journal with { Phase = JournalPhase.Applying, Revision = 2 }, 1));
        Assert.Throws<IOException>(() => registry.Advance(journal with { Phase = JournalPhase.Committed, Revision = 2 }, 1, new(true, false, false, false)));
        AssertPending(database, journal);
    }

    private static void AssertGate(EffectGateSnapshot gate, StateDatabase database, RegisteredJournal journal, EffectGateState state)
    {
        Assert.Equal(database.InstanceId, gate.InstanceId);
        Assert.Equal(journal.JournalId, gate.JournalId); Assert.Equal(journal.OperationId, gate.OperationId);
        Assert.Equal(journal.Kind, gate.Kind); Assert.Equal(journal.PlanDigest, gate.PlanDigest);
        Assert.Equal(state, gate.State);
    }

    private static void AssertPending(StateDatabase database, RegisteredJournal journal)
    {
        var registry = new RecoveryJournalRegistry(database);
        AssertRegistration(registry, journal);
        var isolation = Assert.Single(registry.ReadSnapshot().Isolations);
        Assert.Equal(journal.JournalId, isolation.JournalId);
        Assert.Equal(Assert.Single(journal.ResourceKeys), isolation.ResourceKey);
    }

    private static void AssertRegistration(RecoveryJournalRegistry registry, RegisteredJournal journal)
    {
        var actual = registry.ReadOperationJournal(journal.OperationId);
        Assert.Equal(journal.ResourceKeys, actual.ResourceKeys);
        Assert.Equal(journal with { ResourceKeys = actual.ResourceKeys }, actual);
    }

    private static (StateDatabase Database, RegisteredJournal Journal, OperationIdentity Operation) Create(JournalKind kind)
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture-effect-admission", new string('a', 64), "fixture.effect-gate");
        var digest = new string('c', 64);
        var journal = new RegisteredJournal(Guid.NewGuid(), operation.OperationId, kind, JournalPhase.Prepared, 1,
            $"blobs/plans/{digest}.json", digest, ["fixture:effect-gate"]);
        var accepted = new RecoveryJournalRegistry(database).AcceptPrepared(operation, journal, [], DateTimeOffset.UtcNow);
        Assert.True(accepted.Acceptance.IsNew);
        return (database, journal, operation);
    }
}
