using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class EffectOutboxTests
{
    [Fact(DisplayName = "Concurrent Compound Acceptance Publishes One Operation Journal Isolation And Effect Set")]
    public async Task AcceptsSingleOutbox()
    {
        var fixture = Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var journal = fixture.Journal with { JournalId = Guid.NewGuid() };
            return fixture.Registry.AcceptPrepared(fixture.Operation, journal,
                [fixture.Effect with { JournalId = journal.JournalId }], DateTimeOffset.UtcNow);
        })));
        Assert.Single(results, result => result.Acceptance.IsNew);
        var snapshot = fixture.Registry.ReadSnapshot();
        var journal = Assert.Single(snapshot.Journals);
        Assert.All(results, result => Assert.Equal(journal.JournalId, result.JournalId));
        Assert.Single(snapshot.Isolations);
        var effect = Assert.Single(fixture.Outbox.Read(journal.JournalId));
        Assert.Equal(FileEffectState.Pending, effect.State);
        Assert.Equal(fixture.Effect.IntentDigest, effect.IntentDigest);
        Assert.Throws<OperationConflictException>(() => fixture.Registry.AcceptPrepared(
            new(fixture.Operation.OperationId, "other-subject", fixture.Operation.RequestDigest, fixture.Operation.Kind), fixture.Journal,
            [fixture.Effect], DateTimeOffset.UtcNow));
        Assert.Single(fixture.Registry.ReadSnapshot().Journals);
    }

    [Fact(DisplayName = "Failed Outbox Registration Rolls Back The Whole Acceptance Including Operation Identity")]
    public void RevertsFailedRegistration()
    {
        var fixture = Create();
        var invalid = fixture.Effect with { StepId = "invalid/step" };
        Assert.Throws<ArgumentException>(() => fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal,
            [fixture.Effect, invalid], DateTimeOffset.UtcNow));
        var snapshot = fixture.Registry.ReadSnapshot();
        Assert.Empty(snapshot.Journals);
        Assert.Empty(snapshot.Isolations);
        Assert.Empty(fixture.Outbox.Read(fixture.Journal.JournalId));
        Assert.Equal(1, snapshot.Revision);
        Assert.True(fixture.Database.Accept(fixture.Operation, DateTimeOffset.UtcNow).IsNew);
    }

    [Fact(DisplayName = "Effect Receipts Use Compare And Swap And An Unclosed Effect Cannot Release Isolation")]
    public void PreservesUnclosedIsolation()
    {
        var fixture = Create();
        fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [fixture.Effect], DateTimeOffset.UtcNow);
        var journal = fixture.Journal with { Revision = 2, Phase = JournalPhase.Applying };
        new JournalEffectGate(fixture.Database).Enter(fixture.Journal, 1);
        fixture.Registry.Advance(journal, 1);
        fixture.Outbox.Record(journal.JournalId, fixture.Effect.StepId, FileEffectState.Pending, FileEffectState.Started, new string('c', 64));
        Assert.Throws<IOException>(() => fixture.Outbox.Record(journal.JournalId, fixture.Effect.StepId,
            FileEffectState.Pending, FileEffectState.Started, new string('d', 64)));
        journal = journal with { Revision = 3, Phase = JournalPhase.Applied };
        fixture.Registry.Advance(journal, 2);
        Assert.Throws<IOException>(() => fixture.Registry.Advance(journal with { Revision = 4, Phase = JournalPhase.Committed }, 3));
        fixture.Outbox.Record(journal.JournalId, fixture.Effect.StepId, FileEffectState.Started, FileEffectState.Completed, new string('e', 64));
        fixture.Registry.Advance(journal with { Revision = 4, Phase = JournalPhase.Committed }, 3);
        Assert.Empty(fixture.Registry.ReadSnapshot().Isolations);
        Assert.Equal(new string('e', 64), Assert.Single(fixture.Outbox.Read(journal.JournalId)).ReceiptDigest);
    }

    [Fact(DisplayName = "An Applied Journal With Pending Effect Stays Isolated And Cannot Start Late Effects")]
    public void RejectsLateEffects()
    {
        var fixture = Create();
        fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [fixture.Effect], DateTimeOffset.UtcNow);
        new JournalEffectGate(fixture.Database).Enter(fixture.Journal, 1);
        fixture.Registry.Advance(fixture.Journal with { Phase = JournalPhase.Applying, Revision = 2 }, 1);
        fixture.Registry.Advance(fixture.Journal with { Phase = JournalPhase.Applied, Revision = 3 }, 2);
        Assert.Throws<IOException>(() => fixture.Outbox.Record(fixture.Journal.JournalId, fixture.Effect.StepId,
            FileEffectState.Pending, FileEffectState.Started, new string('c', 64)));
        Assert.Throws<IOException>(() => fixture.Registry.Advance(fixture.Journal with { Phase = JournalPhase.Committed, Revision = 4 }, 3));
        Assert.Single(fixture.Registry.ReadSnapshot().Isolations);
        Assert.Equal(FileEffectState.Pending, Assert.Single(fixture.Outbox.Read(fixture.Journal.JournalId)).State);
    }

    [Fact(DisplayName = "A Not Executed Effect Requires Positive Complete Evidence Before Terminal Commit")]
    public void RequiresUnexecutedProof()
    {
        var fixture = Create();
        fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [fixture.Effect], DateTimeOffset.UtcNow);
        Assert.Throws<IOException>(() => fixture.Outbox.Record(fixture.Journal.JournalId, fixture.Effect.StepId,
            FileEffectState.Pending, FileEffectState.NotExecuted, new string('c', 64)));
        var evidence = new NoEffectEvidence(true, false, false, false);
        new JournalEffectGate(fixture.Database).SealUnentered(fixture.Journal, 1);
        fixture.Outbox.Record(fixture.Journal.JournalId, fixture.Effect.StepId, FileEffectState.Pending, FileEffectState.NotExecuted, new string('c', 64), evidence);
        fixture.Registry.Advance(fixture.Journal with { Revision = 2, Phase = JournalPhase.Committed }, 1, evidence);
        Assert.Empty(fixture.Registry.ReadSnapshot().Isolations);
        Assert.Equal(FileEffectState.NotExecuted, Assert.Single(fixture.Outbox.Read(fixture.Journal.JournalId)).State);
    }

    [Fact(DisplayName = "Replaying The Original Acceptance Keeps Its First Journal After An Independent Physical Attempt")]
    public void ReplaysOriginalJournal()
    {
        var fixture = Create();
        fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [fixture.Effect], DateTimeOffset.UtcNow);
        fixture.Registry.RegisterPrepared(fixture.Journal with { JournalId = Guid.NewGuid() });
        var replay = fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal with { JournalId = Guid.NewGuid() },
            [fixture.Effect], DateTimeOffset.UtcNow);
        Assert.False(replay.Acceptance.IsNew);
        Assert.Equal(fixture.Journal.JournalId, replay.JournalId);
        Assert.Equal(2, fixture.Registry.ReadSnapshot().Journals.Count);
    }

    [Fact(DisplayName = "Cleanup Cannot Commit While Its Primary Remove Owned Effects Remain Open")]
    public void RequiresClosedRemovals()
    {
        var fixture = Create();
        var journal = fixture.Journal with { Kind = JournalKind.FileCleanup };
        var effect = fixture.Effect with { Action = FileEffectAction.RemoveOwned };
        fixture.Registry.AcceptPrepared(fixture.Operation, journal, [effect], DateTimeOffset.UtcNow);
        fixture.Registry.Advance(journal with { Revision = 2, Phase = JournalPhase.Applying }, 1);
        fixture.Registry.Advance(journal with { Revision = 3, Phase = JournalPhase.Applied }, 2);
        Assert.Throws<IOException>(() => fixture.Registry.Advance(journal with { Revision = 4, Phase = JournalPhase.Committed }, 3));
        var snapshot = fixture.Registry.ReadSnapshot();
        Assert.Single(snapshot.Isolations);
        Assert.Throws<InvalidOperationException>(() => RecoveryRegistryPolicy.Verify(snapshot with
        {
            Journals = [journal with { Revision = 4, Phase = JournalPhase.Committed }], Isolations = []
        }));
        fixture.Outbox.Record(journal.JournalId, effect.StepId, FileEffectState.Pending, FileEffectState.Started, new string('c', 64));
        Assert.Throws<IOException>(() => fixture.Registry.Advance(journal with { Revision = 4, Phase = JournalPhase.Committed }, 3));
        fixture.Outbox.Record(journal.JournalId, effect.StepId, FileEffectState.Started, FileEffectState.Completed, new string('d', 64));
        fixture.Registry.Advance(journal with { Revision = 4, Phase = JournalPhase.Committed }, 3);
        Assert.Empty(fixture.Registry.ReadSnapshot().Isolations);
    }

    [Fact(DisplayName = "A Process Only Attempt Can Be Accepted Without Inventing A File Effect")]
    public void AcceptsProcessOnly()
    {
        var fixture = Create();
        fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [], DateTimeOffset.UtcNow);
        Assert.Single(fixture.Registry.ReadSnapshot().Journals);
        Assert.Empty(fixture.Outbox.Read(fixture.Journal.JournalId));
        new JournalEffectGate(fixture.Database).SealUnentered(fixture.Journal, 1);
        fixture.Registry.Advance(fixture.Journal with { Revision = 2, Phase = JournalPhase.Committed }, 1, new(true, false, false, false));
        Assert.Empty(fixture.Registry.ReadSnapshot().Isolations);
    }

    [Fact(DisplayName = "A Cleanup Cannot Accept Another Kinds Destructive File Action")]
    public void RejectsWrongActions()
    {
        var fixture = Create();
        var journal = fixture.Journal with { Kind = JournalKind.FileCleanup };
        Assert.Throws<ArgumentException>(() => fixture.Registry.AcceptPrepared(fixture.Operation, journal, [fixture.Effect], DateTimeOffset.UtcNow));
        Assert.Empty(fixture.Registry.ReadSnapshot().Journals);
        Assert.Empty(fixture.Outbox.Read(journal.JournalId));
        Assert.True(fixture.Database.Accept(fixture.Operation, DateTimeOffset.UtcNow).IsNew);
    }

    [Theory(DisplayName = "Ordinary Outbox Recording Cannot Mint A Reverted Fact")]
    [InlineData(JournalKind.ExtensionApply)]
    [InlineData(JournalKind.UpdateApply)]
    public void RejectsInventedReversion(JournalKind kind)
    {
        var fixture = Create();
        var journal = fixture.Journal with { Kind = kind };
        fixture.Registry.AcceptPrepared(fixture.Operation, journal, [fixture.Effect], DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => fixture.Outbox.Record(journal.JournalId, fixture.Effect.StepId,
            FileEffectState.Pending, FileEffectState.Reverted, new string('c', 64)));
        Assert.Equal(FileEffectState.Pending, Assert.Single(fixture.Outbox.Read(journal.JournalId)).State);
    }

    [Theory(DisplayName = "Sql Cannot Create Reverted Without The Exact Extension Restoration Phase")]
    [InlineData(JournalKind.ExtensionApply)]
    [InlineData(JournalKind.UpdateApply)]
    public void RequiresRestorationPhase(JournalKind kind)
    {
        var fixture = Create();
        var journal = fixture.Journal with { Kind = kind };
        fixture.Registry.AcceptPrepared(fixture.Operation, journal, [fixture.Effect], DateTimeOffset.UtcNow);
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE file_effects SET state='reverted',receipt_digest=$digest WHERE journal_id=$id;";
        command.Parameters.AddWithValue("$id", journal.JournalId.ToString("D"));
        command.Parameters.AddWithValue("$digest", new string('c', 64));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(FileEffectState.Pending, Assert.Single(fixture.Outbox.Read(journal.JournalId)).State);
    }

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(root);
        var database = StateDatabase.CreateFresh(instance);
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture-subject", new string('a', 64), "fixture.file-operation");
        var journal = new RegisteredJournal(Guid.NewGuid(), operation.OperationId, JournalKind.RunAttempt, JournalPhase.Prepared, 1,
            $"blobs/plans/{new string('b', 64)}.json", new string('b', 64), ["fixture:configuration"]);
        var effect = new FileEffect(journal.JournalId, "preserve-original", FileEffectAction.PreserveOriginal, FileEffectState.Pending, new string('b', 64), null);
        return new(database, operation, journal, effect, new(database), new(database));
    }

    private sealed record Fixture(StateDatabase Database, OperationIdentity Operation, RegisteredJournal Journal,
        FileEffect Effect, RecoveryJournalRegistry Registry, FileEffectOutbox Outbox);
}
