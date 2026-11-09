using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using static NexusPipeline.StorageTests.InitialRecoveryTests;

namespace NexusPipeline.StorageTests;

public sealed class EffectOutboxTests
{
    [Fact(DisplayName = "Concurrent Compound Acceptance Publishes One Operation Journal Isolation And Effect Set")]
    public async Task AcceptsSingleOutbox()
    {
        var fixture = Create();
        var requests = Enumerable.Range(0, 8).Select(_ => fixture.Journal with { JournalId = Guid.NewGuid() }).ToArray();
        var results = await Task.WhenAll(requests.Select(journal => Task.Run(() => fixture.Registry.AcceptPrepared(fixture.Operation, journal,
            [fixture.Effect with { JournalId = journal.JournalId }], DateTimeOffset.UtcNow))));
        var winner = Assert.Single(results, result => result.Acceptance.IsNew);
        var journal = Assert.Single(requests, request => request.JournalId == winner.JournalId);
        Assert.All(results, result =>
        {
            Assert.Equal(journal.JournalId, result.JournalId);
            Assert.Equal(fixture.Operation.OperationId, result.Acceptance.OperationId);
        });
        foreach (var request in requests.Where(item => item.JournalId != winner.JournalId))
            Assert.Null(fixture.Registry.FindJournal(request.JournalId));
        var effect = fixture.Effect with { JournalId = journal.JournalId };
        Assert.Equal(effect, Assert.Single(fixture.Outbox.Read(journal.JournalId)));
        AssertIsolated(fixture, journal);
        var impostor = fixture.Journal with { JournalId = Guid.NewGuid() };
        Assert.Throws<OperationConflictException>(() => fixture.Registry.AcceptPrepared(
            new(fixture.Operation.OperationId, "other-subject", fixture.Operation.RequestDigest, fixture.Operation.Kind), impostor,
            [fixture.Effect with { JournalId = impostor.JournalId }], DateTimeOffset.UtcNow));
        Assert.Null(fixture.Registry.FindJournal(impostor.JournalId));
        AssertIsolated(fixture, journal);
        Assert.Equal(effect, Assert.Single(fixture.Outbox.Read(journal.JournalId)));
    }

    [Fact(DisplayName = "Failed Outbox Registration Rolls Back The Whole Acceptance Including Operation Identity")]
    public void RevertsFailedRegistration()
    {
        var fixture = Create();
        var invalid = fixture.Effect with { StepId = "invalid/step" };
        Assert.Throws<ArgumentException>(() => fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal,
            [fixture.Effect, invalid], DateTimeOffset.UtcNow));
        AssertUnaccepted(fixture);
        var retry = fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [fixture.Effect], DateTimeOffset.UtcNow);
        Assert.True(retry.Acceptance.IsNew);
        Assert.Equal(fixture.Journal.JournalId, retry.JournalId);
        Assert.Equal(fixture.Effect, Assert.Single(fixture.Outbox.Read(retry.JournalId)));
        AssertIsolated(fixture, fixture.Journal);
    }

    [Fact(DisplayName = "Effect Receipts Use Compare And Swap And An Unclosed Effect Cannot Release Isolation")]
    public void PreservesUnclosedIsolation()
    {
        var fixture = Create();
        fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [fixture.Effect], DateTimeOffset.UtcNow);
        var journal = fixture.Journal with { Revision = 2, Phase = JournalPhase.Applying };
        new JournalEffectGate(fixture.Database).Enter(fixture.Journal, 1);
        fixture.Registry.Advance(journal, 1);
        var started = new string('c', 64);
        fixture.Outbox.Record(journal.JournalId, fixture.Effect.StepId, FileEffectState.Pending, FileEffectState.Started, started);
        Assert.Equal("file_effect_state_conflict", Assert.Throws<IOException>(() => fixture.Outbox.Record(journal.JournalId, fixture.Effect.StepId,
            FileEffectState.Pending, FileEffectState.Started, new string('d', 64))).Message);
        journal = journal with { Revision = 3, Phase = JournalPhase.Applied };
        fixture.Registry.Advance(journal, 2);
        AssertUnclosed(fixture, journal);
        Assert.Equal(fixture.Effect with { State = FileEffectState.Started, ReceiptDigest = started }, Assert.Single(fixture.Outbox.Read(journal.JournalId)));
        var completed = new string('e', 64);
        fixture.Outbox.Record(journal.JournalId, fixture.Effect.StepId, FileEffectState.Started, FileEffectState.Completed, completed);
        var terminal = journal with { Revision = 4, Phase = JournalPhase.Committed };
        fixture.Registry.Advance(terminal, 3);
        AssertRegistration(fixture.Registry, terminal, JournalPhase.Committed);
        Assert.Empty(fixture.Registry.ReadSnapshot().Isolations);
        Assert.Equal(fixture.Effect with { State = FileEffectState.Completed, ReceiptDigest = completed }, Assert.Single(fixture.Outbox.Read(journal.JournalId)));
    }

    [Fact(DisplayName = "An Applied Journal With Pending Effect Stays Isolated And Cannot Start Late Effects")]
    public void RejectsLateEffects()
    {
        var fixture = Create();
        fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [fixture.Effect], DateTimeOffset.UtcNow);
        new JournalEffectGate(fixture.Database).Enter(fixture.Journal, 1);
        fixture.Registry.Advance(fixture.Journal with { Phase = JournalPhase.Applying, Revision = 2 }, 1);
        var applied = fixture.Journal with { Phase = JournalPhase.Applied, Revision = 3 };
        fixture.Registry.Advance(applied, 2);
        Assert.Equal("effect_gate_decision_required", Assert.Throws<IOException>(() => fixture.Outbox.Record(fixture.Journal.JournalId, fixture.Effect.StepId,
            FileEffectState.Pending, FileEffectState.Started, new string('c', 64))).Message);
        AssertUnclosed(fixture, applied);
        Assert.Equal(fixture.Effect, Assert.Single(fixture.Outbox.Read(fixture.Journal.JournalId)));
    }

    [Fact(DisplayName = "A Not Executed Effect Requires Positive Complete Evidence Before Terminal Commit")]
    public void RequiresUnexecutedProof()
    {
        var fixture = Create();
        fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [fixture.Effect], DateTimeOffset.UtcNow);
        var receipt = new string('c', 64);
        Assert.Equal("file_effect_no_effect_evidence_required", Assert.Throws<IOException>(() => fixture.Outbox.Record(fixture.Journal.JournalId, fixture.Effect.StepId,
            FileEffectState.Pending, FileEffectState.NotExecuted, receipt)).Message);
        Assert.Equal(fixture.Effect, Assert.Single(fixture.Outbox.Read(fixture.Journal.JournalId)));
        AssertIsolated(fixture, fixture.Journal);
        var evidence = new NoEffectEvidence(true, false, false, false);
        new JournalEffectGate(fixture.Database).SealUnentered(fixture.Journal, 1);
        fixture.Outbox.Record(fixture.Journal.JournalId, fixture.Effect.StepId, FileEffectState.Pending, FileEffectState.NotExecuted, receipt, evidence);
        fixture.Registry.Advance(fixture.Journal with { Revision = 2, Phase = JournalPhase.Committed }, 1, evidence);
        Assert.Empty(fixture.Registry.ReadSnapshot().Isolations);
        Assert.Equal(fixture.Effect with { State = FileEffectState.NotExecuted, ReceiptDigest = receipt }, Assert.Single(fixture.Outbox.Read(fixture.Journal.JournalId)));
    }

    [Fact(DisplayName = "Replaying The Original Acceptance Keeps Its First Journal After An Independent Physical Attempt")]
    public void ReplaysOriginalJournal()
    {
        var fixture = Create();
        fixture.Registry.AcceptPrepared(fixture.Operation, fixture.Journal, [fixture.Effect], DateTimeOffset.UtcNow);
        var independent = fixture.Journal with { JournalId = Guid.NewGuid() };
        fixture.Registry.RegisterPrepared(independent);
        var proposed = fixture.Journal with { JournalId = Guid.NewGuid() };
        var before = fixture.Registry.ReadSnapshot().Isolations;
        var replay = fixture.Registry.AcceptPrepared(fixture.Operation, proposed,
            [fixture.Effect with { JournalId = proposed.JournalId }], DateTimeOffset.UtcNow);
        Assert.False(replay.Acceptance.IsNew);
        Assert.Equal(fixture.Operation.OperationId, replay.Acceptance.OperationId);
        Assert.Equal(fixture.Journal.JournalId, replay.JournalId);
        Assert.Equal(fixture.Effect, Assert.Single(fixture.Outbox.Read(replay.JournalId)));
        Assert.Null(fixture.Registry.FindJournal(proposed.JournalId));
        Assert.Equal(fixture.Operation.OperationId, fixture.Registry.ReadJournal(independent.JournalId).OperationId);
        Assert.Empty(fixture.Outbox.Read(independent.JournalId));
        Assert.Equal(before, fixture.Registry.ReadSnapshot().Isolations);
    }

    [Fact(DisplayName = "Cleanup Cannot Commit While Its Primary Remove Owned Effects Remain Open")]
    public void RequiresClosedRemovals()
    {
        var fixture = Create();
        var journal = fixture.Journal with { Kind = JournalKind.FileCleanup };
        var effect = fixture.Effect with { Action = FileEffectAction.RemoveOwned };
        fixture.Registry.AcceptPrepared(fixture.Operation, journal, [effect], DateTimeOffset.UtcNow);
        fixture.Registry.Advance(journal with { Revision = 2, Phase = JournalPhase.Applying }, 1);
        var applied = journal with { Revision = 3, Phase = JournalPhase.Applied };
        fixture.Registry.Advance(applied, 2);
        AssertUnclosed(fixture, applied);
        var snapshot = fixture.Registry.ReadSnapshot();
        Assert.Equal("recovery_registry_terminal_effect_unclosed", Assert.Throws<InvalidOperationException>(() => RecoveryRegistryPolicy.Verify(snapshot with
        {
            Journals = [journal with { Revision = 4, Phase = JournalPhase.Committed }], Isolations = []
        })).Message);
        fixture.Outbox.Record(journal.JournalId, effect.StepId, FileEffectState.Pending, FileEffectState.Started, new string('c', 64));
        AssertUnclosed(fixture, applied);
        fixture.Outbox.Record(journal.JournalId, effect.StepId, FileEffectState.Started, FileEffectState.Completed, new string('d', 64));
        fixture.Registry.Advance(journal with { Revision = 4, Phase = JournalPhase.Committed }, 3);
        Assert.Empty(fixture.Registry.ReadSnapshot().Isolations);
        Assert.Equal(effect with { State = FileEffectState.Completed, ReceiptDigest = new string('d', 64) }, Assert.Single(fixture.Outbox.Read(journal.JournalId)));
    }

    [Fact(DisplayName = "A Cleanup Cannot Accept Another Kinds Destructive File Action")]
    public void RejectsWrongActions()
    {
        var fixture = Create();
        var journal = fixture.Journal with { Kind = JournalKind.FileCleanup };
        Assert.Throws<ArgumentException>(() => fixture.Registry.AcceptPrepared(fixture.Operation, journal, [fixture.Effect], DateTimeOffset.UtcNow));
        AssertUnaccepted(fixture);
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
        Assert.Equal(fixture.Effect, Assert.Single(fixture.Outbox.Read(journal.JournalId)));
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
        var rejected = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("file_effect_reverted_unbound", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(fixture.Effect, Assert.Single(fixture.Outbox.Read(journal.JournalId)));
    }

    private static void AssertUnclosed(Fixture fixture, RegisteredJournal journal)
    {
        var terminal = journal with { Phase = JournalPhase.Committed, Revision = journal.Revision + 1 };
        Assert.Equal("file_effects_not_closed", Assert.Throws<IOException>(() => fixture.Registry.Advance(terminal, journal.Revision)).Message);
        AssertIsolated(fixture, journal);
    }

    private static void AssertIsolated(Fixture fixture, RegisteredJournal journal)
    {
        Assert.Equal(journal.Revision, AssertRegistration(fixture.Registry, journal, journal.Phase).Revision);
        var isolation = Assert.Single(fixture.Registry.ReadSnapshot().Isolations);
        Assert.Equal(journal.JournalId, isolation.JournalId);
        Assert.Equal(Assert.Single(journal.ResourceKeys), isolation.ResourceKey);
    }

    private static void AssertUnaccepted(Fixture fixture)
    {
        Assert.Null(fixture.Registry.FindJournal(fixture.Journal.JournalId));
        Assert.Empty(fixture.Outbox.Read(fixture.Journal.JournalId));
        Assert.DoesNotContain(fixture.Registry.ReadSnapshot().Isolations, item => item.JournalId == fixture.Journal.JournalId);
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Null(OperationRows.ReadExisting(connection, transaction, fixture.Operation));
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
