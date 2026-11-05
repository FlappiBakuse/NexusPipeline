using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RecoveryRegistryTests
{
    [Fact(DisplayName = "An Unknown Journal Root File Cannot Be Treated As An Empty Or Uncreated Journal Directory")]
    public void RejectsUnknownRoots()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var root = Path.GetDirectoryName(Path.GetDirectoryName(database.DatabasePath))!;
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenExisting(root);
        File.WriteAllText(Path.Combine(root, "journals"), "preserve unknown journal root");
        using var observation = new DatabaseObservation(database);
        var before = Fingerprints(root);
        var registry = new RecoveryJournalRegistry(database);
        var evidence = new JournalRecoveryEvidence(instance, new(instance, contracts));
        Assert.Throws<IOException>(() => evidence.VerifyRecognizedAll(registry.ReadSnapshot().Journals));
        var index = new RecoveryIndex();
        Assert.Throws<IOException>(() => new RecoveryCoordinator(registry, evidence, index).VerifyAndPublish());
        Assert.False(index.Current.IsReady);
        Assert.Equal(before, Fingerprints(root));
    }

    [Fact(DisplayName = "Recovery Preflight Accepts Only An Exact Recorded Sql Prefix Without Publishing Or Changing Ahead Physical Evidence")]
    public void ReadsExactPrefix()
    {
        var fixture = Create();
        fixture.Document["revision"] = 2;
        fixture.Document["phase"] = "applying";
        fixture.Store.Append(fixture.Registered.JournalId, 1, JsonSerializer.SerializeToUtf8Bytes(fixture.Document));
        using var observation = new DatabaseObservation(fixture.Database);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var evidence = new JournalRecoveryEvidence(fixture.Instance, fixture.Store);
        evidence.VerifyRecognizedAll(fixture.Registry.ReadSnapshot().Journals);
        Assert.Throws<IOException>(() => evidence.VerifyAll(fixture.Registry.ReadSnapshot().Journals));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.False(fixture.Registry.ReadSnapshot().IsReady);
    }

    [Theory(DisplayName = "Recovery Preflight Preserves Every Byte When Its Sql Prefix Or Complete Physical History Cannot Be Verified")]
    [InlineData("sql-ahead")]
    [InlineData("unknown-plan")]
    [InlineData("torn-revision")]
    public void PreservesUnverifiedEvidence(string fault)
    {
        var fixture = Create();
        if (fault == "sql-ahead") fixture.Registry.Advance(fixture.Registered with { Revision = 2, Phase = JournalPhase.Applying }, 1);
        else if (fault == "unknown-plan") File.WriteAllText(fixture.PlanPath, "preserve unknown plan");
        else File.WriteAllText(Path.Combine(fixture.JournalDirectory, "0000000002.json"), "{\"format\":");
        using var observation = new DatabaseObservation(fixture.Database);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var evidence = new JournalRecoveryEvidence(fixture.Instance, fixture.Store);
        Assert.ThrowsAny<Exception>(() => evidence.VerifyRecognizedAll(fixture.Registry.ReadSnapshot().Journals));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.False(fixture.Registry.ReadSnapshot().IsReady);
    }

    [Fact(DisplayName = "Prepared Journal And Isolation Are Committed Together And Survive A New Registry")]
    public void CommitsJournalIsolation()
    {
        var fixture = Create();
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.False(snapshot.IsReady);
        Assert.Equal(2, snapshot.Revision);
        var journal = Assert.Single(snapshot.Journals);
        Assert.Equal(fixture.Registered.JournalId, journal.JournalId);
        Assert.Equal("configuration:fixture", Assert.Single(snapshot.Isolations).ResourceKey);
        Assert.Equal(fixture.Registered.PlanDigest, journal.PlanDigest);
        Assert.Throws<SqliteException>(() => fixture.Registry.RegisterPrepared(fixture.Registered with { JournalId = Guid.NewGuid(), OperationId = Guid.NewGuid() }));
        Assert.Single(fixture.Registry.ReadSnapshot().Journals);
        Assert.Equal(2, fixture.Registry.ReadSnapshot().Revision);
    }

    [Fact(DisplayName = "Stale Revisions Cannot Publish Readiness Or Release An Unfinished Isolation")]
    public void RejectsStaleReadiness()
    {
        var fixture = Create();
        Assert.Throws<IOException>(() => fixture.Registry.PublishVerified(1));
        var updated = fixture.Registered with { Revision = 2, Phase = JournalPhase.Applying };
        fixture.Registry.Advance(updated, 1);
        Assert.Throws<IOException>(() => fixture.Registry.Advance(updated with { Revision = 3 }, 1));
        Assert.Throws<IOException>(() => fixture.Registry.Advance(updated with { Phase = JournalPhase.Committed, Revision = 3 }, 2));
        var snapshot = fixture.Registry.ReadSnapshot();
        Assert.False(snapshot.IsReady);
        Assert.Single(snapshot.Isolations);
        Assert.Equal(JournalPhase.Applying, Assert.Single(snapshot.Journals).Phase);
    }

    [Fact(DisplayName = "A Verified Terminal Commit Releases Only Its Registered Resources")]
    public void ReleasesRegisteredResources()
    {
        var fixture = Create();
        var evidence = new NoEffectEvidence(true, false, false, false);
        fixture.Registry.Advance(fixture.Registered with { Revision = 2, Phase = JournalPhase.Committed }, 1, evidence);
        var snapshot = fixture.Registry.ReadSnapshot();
        Assert.Empty(snapshot.Isolations);
        Assert.Equal(JournalPhase.Committed, Assert.Single(snapshot.Journals).Phase);
        Assert.Single(Assert.Single(snapshot.Journals).ResourceKeys);
        Assert.False(snapshot.IsReady);
    }

    [Fact(DisplayName = "Verified Physical Agreement Publishes An Immutable Scoped Projection")]
    public void FreezesVerifiedProjection()
    {
        var fixture = Create();
        var index = new RecoveryIndex();
        var coordinator = new RecoveryCoordinator(fixture.Registry, new JournalRecoveryEvidence(fixture.Instance, fixture.Store), index);
        var result = coordinator.VerifyAndPublish();
        Assert.True(result.IsReady);
        Assert.True(index.IsBlocked(new HashSet<string> { "configuration:fixture" }, out _));
        Assert.False(index.IsBlocked(new HashSet<string> { "configuration:other" }, out _));
        for (var count = 0; count < 5; count++) index.BlockUnknown("fixture_invalidation");
        var republished = coordinator.VerifyAndPublish();
        Assert.True(republished.Revision > result.Revision);
        Assert.True(fixture.Registry.ReadSnapshot().IsReady);
    }

    [Theory(DisplayName = "Unverified Input Globally Blocks Admission And Leaves Database And Journal Bytes Untouched")]
    [InlineData("unknown-entry")]
    [InlineData("unknown-phase")]
    [InlineData("valid-but-unreconciled-revision")]
    [InlineData("changed-plan")]
    public void BlocksUnverifiedAdmission(string fault)
    {
        var fixture = Create();
        if (fault == "unknown-entry") File.WriteAllText(Path.Combine(fixture.Instance.RuntimeRoot, "journals", "foreign.data"), "preserve");
        else if (fault == "changed-plan") File.WriteAllText(fixture.PlanPath, "unrecognized");
        else
        {
            fixture.Document["revision"] = 2;
            fixture.Document["phase"] = fault == "unknown-phase" ? "oldPhase" : "applying";
            File.WriteAllBytes(Path.Combine(fixture.JournalDirectory, "0000000002.json"), JsonSerializer.SerializeToUtf8Bytes(fixture.Document));
        }
        var index = new RecoveryIndex();
        using var observation = new DatabaseObservation(fixture.Database);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var coordinator = new RecoveryCoordinator(fixture.Registry, new JournalRecoveryEvidence(fixture.Instance, fixture.Store), index);
        Assert.ThrowsAny<Exception>(() => coordinator.VerifyAndPublish());
        Assert.False(index.Current.IsReady);
        Assert.True(index.IsBlocked(new HashSet<string>(), out _));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.False(fixture.Registry.ReadSnapshot().IsReady);
    }

    [Theory(DisplayName = "Incoherent Persisted Recovery Facts Cannot Publish Admission Or Be Repaired During Identification")]
    [InlineData("missing-isolation")]
    [InlineData("foreign-isolation")]
    [InlineData("unknown-effect-digest")]
    [InlineData("terminal-unclosed-effect")]
    public void RejectsIncoherentFacts(string fault)
    {
        var fixture = Create();
        using (var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$id", fixture.Registered.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$digest", new string('a', 64));
            command.Parameters.AddWithValue("$unknown", new string('z', 64));
            command.CommandText = fault switch
            {
                "missing-isolation" => "DELETE FROM resource_isolations WHERE journal_id=$id;",
                "foreign-isolation" => "INSERT INTO resource_isolations VALUES('unknown-resource',$id,'verified','unknown');",
                "unknown-effect-digest" => "INSERT INTO file_effects VALUES($id,'fixture-step','installCandidate','pending',$unknown,NULL);",
                _ => "INSERT INTO file_effects VALUES($id,'fixture-step','installCandidate','started',$digest,$digest); " +
                    "UPDATE operation_journals SET phase='committed',revision=2 WHERE journal_id=$id; DELETE FROM resource_isolations WHERE journal_id=$id;"
            };
            // Fund the deliberately corrupt row so this fixture reaches recovery-content validation.
            if (fault is "unknown-effect-digest" or "terminal-unclosed-effect")
                command.CommandText = "UPDATE recovery_capacity_reservations SET effect_limit=1,remaining_effects=1 WHERE owner_journal_id=$id;" + command.CommandText;
            command.ExecuteNonQuery();
        }
        using var observed = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
        using var observation = observed.BeginTransaction(deferred: true);
        using (var probe = observed.CreateCommand())
        {
            probe.Transaction = observation; probe.CommandText = "SELECT count(*) FROM operation_journals;";
            _ = probe.ExecuteScalar();
        }
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var index = new RecoveryIndex();
        var coordinator = new RecoveryCoordinator(fixture.Registry, new JournalRecoveryEvidence(fixture.Instance, fixture.Store), index);
        if (fault == "terminal-unclosed-effect") Assert.Throws<IOException>(() => coordinator.VerifyAndPublish());
        else Assert.Throws<InvalidOperationException>(() => coordinator.VerifyAndPublish());
        Assert.True(index.IsBlocked(new HashSet<string>(), out _));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        var database = StateDatabase.CreateFresh(instance);
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture-subject", new string('a', 64), "configuration.exchange");
        database.Accept(operation, DateTimeOffset.UtcNow);
        var registry = new RecoveryJournalRegistry(database);
        var digest = Convert.ToHexStringLower(SHA256.HashData("{}"u8));
        var planPath = $"blobs/plans/{digest}.json";
        var fullPlan = Path.Combine(root, planPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPlan)!);
        File.WriteAllText(fullPlan, "{}");
        var registered = new RegisteredJournal(Guid.NewGuid(), operation.OperationId, JournalKind.ConfigExchange, JournalPhase.Prepared, 1, planPath, digest, ["configuration:fixture"]);
        registry.RegisterPrepared(registered);
        var document = new JsonObject
        {
            ["format"] = "nexus.execution.journal", ["version"] = 1,
            ["instanceId"] = instance.InstanceId.ToString("D"), ["journalId"] = registered.JournalId.ToString("D"),
            ["operationId"] = operation.OperationId.ToString("D"), ["operationKind"] = "configExchange",
            ["phase"] = "prepared", ["revision"] = 1, ["updatedAt"] = "2026-10-19T00:00:00Z",
            ["planPath"] = planPath, ["planDigest"] = digest, ["resourceKeys"] = new JsonArray("configuration:fixture"),
            ["participants"] = new JsonArray(), ["receipts"] = new JsonArray()
        };
        var store = new JournalStore(instance, contracts);
        store.Create(JsonSerializer.SerializeToUtf8Bytes(document));
        return new(instance, database, registry, store, registered, document, fullPlan, Path.Combine(root, "journals", registered.JournalId.ToString("D")));
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path =>
        {
            // SQLite holds SHM for read/write even while this observer only reads.
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(input));
        }).ToArray();
    private sealed record Fixture(RuntimeInstance Instance, StateDatabase Database, RecoveryJournalRegistry Registry, JournalStore Store,
        RegisteredJournal Registered, JsonObject Document, string PlanPath, string JournalDirectory);
}
