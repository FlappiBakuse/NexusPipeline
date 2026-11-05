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
        Assert.Equal(database.InstanceId, initial.InstanceId);
        Assert.Equal(journal.OperationId, initial.OperationId);
        Assert.Equal(journal.PlanDigest, initial.PlanDigest);
        Assert.Equal(EffectGateState.Unentered, initial.State);
        var before = new RecoveryJournalRegistry(database).ReadSnapshot();
        var sealedGate = gate.SealUnentered(journal, 1);
        Assert.Equal(EffectGateState.Sealed, sealedGate.State);
        Assert.Equal(2, sealedGate.Revision);
        Assert.Equal(sealedGate, new PersistentEffectGate(database).Read(journal.JournalId));
        Assert.Equal(sealedGate, gate.SealUnentered(journal, 2));
        Assert.Throws<IOException>(() => gate.Enter(journal, 2));
        Assert.Throws<IOException>(() => gate.Enter(journal, 1));
        var after = new RecoveryJournalRegistry(database).ReadSnapshot();
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.False(after.IsReady);
        Assert.Equal(JournalPhase.Prepared, Assert.Single(after.Journals).Phase);
        Assert.Equal(before.Isolations, after.Isolations);
    }

    [Theory(DisplayName = "Entered Gate Cannot Be Resealed Or Grant A Second Executor")]
    [InlineData(JournalKind.RunAttempt)]
    [InlineData(JournalKind.UpdateApply)]
    [InlineData(JournalKind.ExtensionApply)]
    public void RejectsRepeatedEntry(JournalKind kind)
    {
        var (database, journal, _) = Create(kind);
        var gate = new PersistentEffectGate(database);
        Assert.Equal(EffectGateState.Entered, gate.Enter(journal, 1).State);
        Assert.Throws<IOException>(() => gate.SealUnentered(journal, 2));
        Assert.Throws<IOException>(() => gate.Enter(journal, 2));
        Assert.Equal(EffectGateState.Entered, new PersistentEffectGate(database).Read(journal.JournalId).State);
        Assert.Single(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations);
    }

    [Theory(DisplayName = "Concurrent Entry And Sealing Have Exactly One Durable Winner")]
    [InlineData(JournalKind.RunAttempt)]
    [InlineData(JournalKind.UpdateApply)]
    [InlineData(JournalKind.ExtensionApply)]
    public async Task SerializesGateDecision(JournalKind kind)
    {
        var (database, journal, _) = Create(kind);
        using var ready = new Barrier(2);
        var attempts = new[] { EffectGateState.Entered, EffectGateState.Sealed }.Select(next => Task.Run(() =>
        {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(10)));
            var gate = new PersistentEffectGate(database);
            try { return next == EffectGateState.Entered ? gate.Enter(journal, 1) : gate.SealUnentered(journal, 1); }
            catch (IOException) { return null; }
        })).ToArray();
        var winner = Assert.Single((await Task.WhenAll(attempts)).OfType<EffectGateSnapshot>());
        Assert.Equal(winner, new PersistentEffectGate(database).Read(journal.JournalId));
        Assert.Equal(2, winner.Revision);
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
        Assert.Throws<IOException>(() => gate.SealUnentered(altered, 1));
        Assert.Throws<IOException>(() => gate.Enter(altered, 1));
        Assert.Equal(before, gate.Read(journal.JournalId));
    }

    [Fact(DisplayName = "Repeated Acceptance Retains The Original Gate And Does Not Reopen It")]
    public void PreservesOriginalGate()
    {
        var (database, journal, operation) = Create(JournalKind.UpdateApply);
        var gate = new PersistentEffectGate(database);
        var sealedGate = gate.SealUnentered(journal, 1);
        var replay = new RecoveryJournalRegistry(database).AcceptPrepared(operation, journal with { JournalId = Guid.NewGuid() }, [], DateTimeOffset.UtcNow);
        Assert.False(replay.Acceptance.IsNew);
        Assert.Equal(journal.JournalId, replay.JournalId);
        Assert.Equal(sealedGate, gate.Read(replay.JournalId));
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
        Assert.Empty(registry.ReadSnapshot().Journals);
        Assert.Throws<IOException>(() => new PersistentEffectGate(database).Read(journal.JournalId));
        Assert.True(registry.AcceptPrepared(operation, journal, [], DateTimeOffset.UtcNow).Acceptance.IsNew);
        Assert.Equal(EffectGateState.Unentered, new PersistentEffectGate(database).Read(journal.JournalId).State);
    }

    [Fact(DisplayName = "Advancing The Journal Prevents Late Entry And Late No Effect Sealing")]
    public void RejectsLateEntry()
    {
        var (database, journal, _) = Create(JournalKind.ExtensionApply);
        var gate = new PersistentEffectGate(database);
        gate.Enter(journal, 1);
        new RecoveryJournalRegistry(database).Advance(journal with { Phase = JournalPhase.Applying, Revision = 2 }, 1);
        Assert.Throws<IOException>(() => gate.Enter(journal, 1));
        Assert.Throws<IOException>(() => gate.SealUnentered(journal, 1));
        Assert.Equal(EffectGateState.Entered, gate.Read(journal.JournalId).State);
        Assert.Single(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations);
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
        Assert.Throws<IOException>(() => registry.Advance(applying, 1));
        Assert.Throws<IOException>(() => registry.Advance(committed, 1, evidence));
        new PersistentEffectGate(database).SealUnentered(journal, 1);
        Assert.Throws<IOException>(() => registry.Advance(applying, 1));
        registry.Advance(committed, 1, evidence);
        Assert.Empty(registry.ReadSnapshot().Isolations);
    }

    [Fact(DisplayName = "A File Effect Cannot Start Before Both Entry And Applying Are Durable")]
    public void RequiresEnteredGate()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture-effect-start", new string('a', 64), "fixture.effect-gate");
        var digest = new string('c', 64);
        var journal = new RegisteredJournal(Guid.NewGuid(), operation.OperationId, JournalKind.UpdateApply, JournalPhase.Prepared, 1,
            $"blobs/plans/{digest}.json", digest, ["fixture:effect-start"]);
        new RecoveryJournalRegistry(database).AcceptPrepared(operation, journal,
            [new(journal.JournalId, "install", FileEffectAction.InstallCandidate, FileEffectState.Pending, digest, null)], DateTimeOffset.UtcNow);
        var outbox = new FileEffectOutbox(database);
        Assert.Throws<IOException>(() => outbox.Record(journal.JournalId, "install", FileEffectState.Pending, FileEffectState.Started, digest));
        new PersistentEffectGate(database).Enter(journal, 1);
        Assert.Throws<IOException>(() => outbox.Record(journal.JournalId, "install", FileEffectState.Pending, FileEffectState.Started, digest));
        new RecoveryJournalRegistry(database).Advance(journal with { Phase = JournalPhase.Applying, Revision = 2 }, 1);
        outbox.Record(journal.JournalId, "install", FileEffectState.Pending, FileEffectState.Started, digest);
        Assert.Equal(FileEffectState.Started, Assert.Single(outbox.Read(journal.JournalId)).State);
    }

    [Theory(DisplayName = "Contradictory Gate State And Revision Cannot Authorize No Effect Evidence")]
    [InlineData("sealed", 1)]
    [InlineData("entered", 1)]
    [InlineData("unentered", 2)]
    public void RejectsContradictoryEvidence(string state, int revision)
    {
        var (database, journal, _) = Create(JournalKind.RunAttempt);
        using (var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE journal_effect_gates SET state=$state,revision=$revision WHERE journal_id=$id;";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$id", journal.JournalId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var before = new RecoveryJournalRegistry(database).ReadSnapshot();
        Assert.Throws<IOException>(() => new PersistentEffectGate(database).Read(journal.JournalId));
        var registry = new RecoveryJournalRegistry(database);
        Assert.Throws<IOException>(() => registry.Advance(journal with { Phase = JournalPhase.Applying, Revision = 2 }, 1));
        Assert.Throws<IOException>(() => registry.Advance(journal with { Phase = JournalPhase.Committed, Revision = 2 }, 1, new(true, false, false, false)));
        Assert.Equal(before.Revision, new RecoveryJournalRegistry(database).ReadSnapshot().Revision);
        Assert.Single(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations);
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
