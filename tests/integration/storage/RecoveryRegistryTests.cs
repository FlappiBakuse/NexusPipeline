using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static NexusPipeline.StorageTests.InitialRecoveryTests;

namespace NexusPipeline.StorageTests;

public sealed class RecoveryRegistryTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Recovery History Fetches Each Ordered Page Without Per-Journal Queries")]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(127, 2)]
    [InlineData(128, 3)]
    [InlineData(129, 4)]
    public void ReadsHistoryPages(int count, int expectedSelects)
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var expected = SeedHistory(database, count);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, allowPooling: false,
            domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        RegisteredJournal[] Read() => RecoveryJournalRegistry.ReadHistoryInside(connection, transaction).ToArray();
        var selects = new List<string>();
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)) selects.Add(sql);
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        RegisteredJournal[] actual;
        try { actual = Read(); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace?)null, null); }
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].ResourceKeys, actual[index].ResourceKeys, StringComparer.Ordinal);
            Assert.Equal(expected[index], actual[index] with { ResourceKeys = expected[index].ResourceKeys });
        }
        const int iterations = 40;
        for (var i = 0; i < 5; i++) _ = Read();
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) _ = Read();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var cpu = process.TotalProcessorTime - cpuBefore;
        output.WriteLine($"journal-history rows={count} selects={selects.Count} repetitions={iterations} elapsed-ms={elapsed.TotalMilliseconds:F3} cpu-ms={cpu.TotalMilliseconds:F3} allocated-bytes={allocated}");
        foreach (var sql in selects.Where(sql => !sql.Contains("journal_id=", StringComparison.Ordinal) ||
                     sql.Contains(expected.FirstOrDefault()?.JournalId.ToString("D") ?? "none", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var planner = command.ExecuteReader();
            while (planner.Read()) output.WriteLine(planner.GetString(3));
        }
        Assert.Equal(expectedSelects, selects.Count);
    }

    [Fact(DisplayName = "A Recovery History Iterator Releases Its Page And Reads Fresh Journal And Resource Facts On The Next Page")]
    public void ReopensHistoryAtPageBoundary()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var expected = SeedHistory(database, 129);
        var registry = new RecoveryJournalRegistry(database);
        using var history = registry.ReadHistory().GetEnumerator();
        Assert.True(history.MoveNext());
        Assert.Equal(expected[0].JournalId, history.Current.JournalId);
        var changed = expected[^1] with { Revision = 2 };
        registry.Advance(changed, 1);
        var added = Assert.Single(SeedHistory(database, 1, offset: 129));
        for (var index = 1; index < 128; index++)
        {
            Assert.True(history.MoveNext());
            Assert.Equal(expected[index].JournalId, history.Current.JournalId);
        }
        Assert.True(history.MoveNext());
        Assert.Equal(changed.JournalId, history.Current.JournalId);
        Assert.Equal(2, history.Current.Revision);
        Assert.True(history.MoveNext());
        Assert.Equal(added.JournalId, history.Current.JournalId);
        Assert.Equal(added.ResourceKeys, history.Current.ResourceKeys, StringComparer.Ordinal);
        Assert.False(history.MoveNext());
    }

    [Fact(DisplayName = "Recovery History Rejects Missing Or Excess Resources And Unknown Journal Enums")]
    public void RejectsDamagedHistoryRows()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var journals = SeedHistory(database, 2);
        using var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: false);
        var id = journals[0].JournalId.ToString("D");
        RegisteredJournal[] Read() => RecoveryJournalRegistry.ReadHistoryInside(connection, transaction).ToArray();
        using var damage = DatabaseConnections.Command(connection, transaction,
            "PRAGMA ignore_check_constraints=ON; UPDATE main.operation_journals SET operation_kind='unknown' WHERE journal_id=$0;",
            journals[1].JournalId.ToString("D"));
        Assert.Equal(1, damage.ExecuteNonQuery());
        using var erase = DatabaseConnections.Command(connection, transaction, "DELETE FROM main.journal_resources WHERE journal_id=$0;", id);
        Assert.Equal(2, erase.ExecuteNonQuery());
        Assert.Equal("recovery_registry_resource_limit", Assert.Throws<IOException>(() => Read()).Message);
        using var populate = DatabaseConnections.Command(connection, transaction,
            "WITH RECURSIVE keys(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM keys WHERE n<4096) " +
            "INSERT INTO main.journal_resources SELECT $0,printf('fixture:%04d',n) FROM keys;", id);
        Assert.Equal(4096, populate.ExecuteNonQuery());
        Assert.Equal(4096, RecoveryJournalRegistry.ReadHistoryInside(connection, transaction).First().ResourceKeys.Count);
        Assert.Equal("recovery_registry_journal_invalid", Assert.Throws<IOException>(() => Read()).Message);
        using var excess = DatabaseConnections.Command(connection, transaction,
            "INSERT INTO main.journal_resources VALUES($0,'fixture:4097');", id);
        Assert.Equal(1, excess.ExecuteNonQuery());
        Assert.Equal("recovery_registry_resource_limit", Assert.Throws<IOException>(() => Read()).Message);
        transaction.Rollback();
        Assert.Equal(2, new RecoveryJournalRegistry(database).ReadHistory().Count());
    }

    [Fact(DisplayName = "Completed Recovery History Selects The Released Family Owner And Preserves The Cursor")]
    public void KeepsCompletedHistoryFilter()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var journals = SeedHistory(database, 3);
        database.Write((connection, transaction) =>
        {
            using var release = DatabaseConnections.Command(connection, transaction,
                "UPDATE main.operation_journals SET phase='committed',revision=2 WHERE journal_id=$0;", journals[1].JournalId.ToString("D"));
            Assert.Equal(1, release.ExecuteNonQuery());
            return true;
        });
        var registry = new RecoveryJournalRegistry(database);
        var selected = Assert.Single(registry.ReadHistory(completedFamiliesOnly: true, after: journals[0].JournalId.ToString("D")));
        Assert.Equal(journals[1].JournalId, selected.JournalId);
        Assert.Equal(JournalPhase.Committed, selected.Phase);
        Assert.Empty(registry.ReadHistory(completedFamiliesOnly: true, after: selected.JournalId.ToString("D")));
    }

    private static RegisteredJournal[] SeedHistory(StateDatabase database, int count, int offset = 0)
    {
        var digest = new string('a', 64);
        var journals = Enumerable.Range(offset + 1, count).Select(index =>
            new RegisteredJournal(Guid.ParseExact($"10000000-0000-4000-8000-{index:D12}", "D"), Guid.NewGuid(),
                JournalKind.ConfigEdit, JournalPhase.Prepared, 1, $"blobs/plans/{digest}.json", digest,
                [$"fixture:{index:D4}-a", $"fixture:{index:D4}-z"])).ToArray();
        var writer = JournalWriterStore.CurrentWriter;
        database.Write((connection, transaction) =>
        {
            foreach (var journal in journals.Reverse())
                RecoveryJournalRegistry.AcceptPreparedInside(connection, transaction,
                    new(journal.OperationId, "fixture:history", journal.PlanDigest, "configuration.edit"), journal, [],
                    DateTimeOffset.UtcNow, null, writer);
            return true;
        });
        return journals;
    }

    [Fact(DisplayName = "History Callbacks Permit Independent Business Commits")]
    public void ReleasesHistoryReads()
    {
        var fixture = Create();
        using var history = fixture.Registry.ReadHistory().GetEnumerator();
        Assert.True(history.MoveNext());
        var identity = new OperationIdentity(Guid.NewGuid(), "test:history-callback", new string('a', 64), "automation.change");
        Assert.True(fixture.Database.Accept(identity, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(fixture.Registered.JournalId, history.Current.JournalId);
        Assert.False(history.MoveNext());
    }

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
        Assert.Equal("journal_root_shape_unrecognized", Assert.Throws<IOException>(() => evidence.VerifyRecognizedAll(registry.ReadSnapshot().Journals)).Message);
        var index = new RecoveryIndex();
        Assert.Equal("journal_root_shape_unrecognized", Assert.Throws<IOException>(() => new RecoveryCoordinator(registry, evidence, index).VerifyAndPublish()).Message);
        Assert.False(index.Current.IsReady);
        Assert.Equal(before, Fingerprints(root));
    }

    [Fact(DisplayName = "Recovery Preflight Accepts Only An Exact Recorded Sql Prefix Without Publishing Or Changing Ahead Physical Evidence")]
    public void ReadsExactPrefix()
    {
        var fixture = Create();
        fixture.Document["revision"] = 2;
        fixture.Document["phase"] = "applying";
        var ahead = fixture.Store.Append(fixture.Registered.JournalId, 1, JsonSerializer.SerializeToUtf8Bytes(fixture.Document));
        var evidence = new JournalRecoveryEvidence(fixture.Instance, fixture.Store);
        var before = AssertRegistration(fixture.Registry, fixture.Registered, JournalPhase.Prepared);
        evidence.VerifyRecognizedAll([before]);
        Assert.Equal("journal_registry_reconciliation_required", Assert.Throws<IOException>(() => evidence.VerifyAll([before])).Message);
        Assert.Equal(before.Revision, AssertRegistration(fixture.Registry, before, JournalPhase.Prepared).Revision);
        Assert.Equal(ahead.Export(), fixture.Store.Read(before.JournalId).Export());
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
        void Verify() => evidence.VerifyRecognizedAll(fixture.Registry.ReadSnapshot().Journals);
        if (fault == "torn-revision") Assert.Equal("invalid_json", Assert.Throws<JsonException>(Verify).Message);
        else
        {
            var reason = fault == "sql-ahead" ? "registered_journal_revision_missing" : "journal_plan_digest_mismatch";
            Assert.Equal(reason, Assert.Throws<IOException>(Verify).Message);
        }
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.False(fixture.Registry.ReadSnapshot().IsReady);
    }

    [Fact(DisplayName = "Prepared Journal And Isolation Are Committed Together And Survive A New Registry")]
    public void CommitsJournalIsolation()
    {
        var fixture = Create();
        var reopened = new RecoveryJournalRegistry(fixture.Database);
        var snapshot = reopened.ReadSnapshot();
        Assert.False(snapshot.IsReady);
        var journal = AssertRegistration(reopened, fixture.Registered, JournalPhase.Prepared);
        AssertIsolation(journal, Assert.Single(snapshot.Isolations));
        var unowned = journal with { JournalId = Guid.NewGuid(), OperationId = Guid.NewGuid() };
        Assert.Throws<SqliteException>(() => fixture.Registry.RegisterPrepared(unowned));
        Assert.Null(reopened.FindJournal(unowned.JournalId));
        var after = reopened.ReadSnapshot();
        Assert.Equal(snapshot.Revision, after.Revision);
        Assert.Equal(snapshot.Isolations, after.Isolations);
        Assert.Equal(journal.Revision, AssertRegistration(reopened, journal, JournalPhase.Prepared).Revision);
    }

    [Fact(DisplayName = "Stale Revisions Cannot Publish Readiness Or Release An Unfinished Isolation")]
    public void RejectsStaleReadiness()
    {
        var fixture = Create();
        var observed = fixture.Registry.ReadSnapshot();
        var updated = fixture.Registered with { Revision = 2, Phase = JournalPhase.Applying };
        fixture.Registry.Advance(updated, 1);
        Assert.Equal("recovery_index_revision_conflict", Assert.Throws<IOException>(() => fixture.Registry.PublishVerified(observed.Revision)).Message);
        Assert.Equal("journal_revision_conflict", Assert.Throws<IOException>(() => fixture.Registry.Advance(updated with { Revision = 3 }, 1)).Message);
        Assert.Equal("journal_transition_rejected", Assert.Throws<IOException>(() =>
            fixture.Registry.Advance(updated with { Phase = JournalPhase.Committed, Revision = 3 }, 2)).Message);
        var snapshot = fixture.Registry.ReadSnapshot();
        Assert.False(snapshot.IsReady);
        AssertIsolation(updated, Assert.Single(snapshot.Isolations));
        Assert.Equal(updated.Revision, AssertRegistration(fixture.Registry, updated, JournalPhase.Applying).Revision);
    }

    [Fact(DisplayName = "A Verified Terminal Commit Releases Only Its Registered Resources")]
    public void ReleasesRegisteredResources()
    {
        var fixture = Create();
        var other = fixture.Registered with { JournalId = Guid.NewGuid(), OperationId = Guid.NewGuid(), ResourceKeys = ["configuration:other"] };
        var operation = new OperationIdentity(other.OperationId, "fixture-subject", other.PlanDigest, "configuration.exchange");
        fixture.Registry.AcceptPrepared(operation, other, [], DateTimeOffset.UtcNow);
        var evidence = new NoEffectEvidence(true, false, false, false);
        fixture.Registry.Advance(fixture.Registered with { Revision = 2, Phase = JournalPhase.Committed }, 1, evidence);
        var snapshot = fixture.Registry.ReadSnapshot();
        AssertRegistration(fixture.Registry, fixture.Registered, JournalPhase.Committed);
        Assert.Equal(other.Revision, AssertRegistration(fixture.Registry, other, JournalPhase.Prepared).Revision);
        AssertIsolation(other, Assert.Single(snapshot.Isolations));
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
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        ResourceClaim[] isolated = [new(Assert.Single(fixture.Registered.ResourceKeys), ResourceClaimMode.Mutation)];
        ResourceClaim[] independent = [new("configuration:other", ResourceClaimMode.Mutation)];
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, resources.TryAcquire(Guid.NewGuid(), isolated).Denial);
        using (var available = resources.TryAcquire(Guid.NewGuid(), independent).Lease) Assert.NotNull(available);
        index.BlockUnknown("fixture_invalidation");
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, resources.TryAcquire(Guid.NewGuid(), independent).Denial);
        Assert.True(result.IsReady);
        AssertIsolation(fixture.Registered, Assert.Single(result.Isolations));
        var republished = coordinator.VerifyAndPublish();
        Assert.True(republished.Revision > result.Revision);
        Assert.Equal(result.Isolations, republished.Isolations);
        using var availableAgain = resources.TryAcquire(Guid.NewGuid(), independent).Lease;
        Assert.NotNull(availableAgain);
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
        var index = new RecoveryIndex();
        var coordinator = new RecoveryCoordinator(fixture.Registry, new JournalRecoveryEvidence(fixture.Instance, fixture.Store), index);
        Assert.True(coordinator.VerifyAndPublish().IsReady);
        if (fault == "unknown-entry") File.WriteAllText(Path.Combine(fixture.Instance.RuntimeRoot, "journals", "foreign.data"), "preserve");
        else if (fault == "changed-plan") File.WriteAllText(fixture.PlanPath, "unrecognized");
        else
        {
            fixture.Document["revision"] = 2;
            fixture.Document["phase"] = fault == "unknown-phase" ? "oldPhase" : "applying";
            File.WriteAllBytes(Path.Combine(fixture.JournalDirectory, "0000000002.json"), JsonSerializer.SerializeToUtf8Bytes(fixture.Document));
        }
        using var observation = new DatabaseObservation(fixture.Database);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        if (fault == "unknown-phase")
            Assert.Equal("unsupported_or_invalid_contract", Assert.Throws<JsonException>(() => coordinator.VerifyAndPublish()).Message);
        else
        {
            var reason = fault switch
            {
                "unknown-entry" => "journal_directory_unknown_entry",
                "changed-plan" => "journal_plan_digest_mismatch",
                _ => "journal_registry_reconciliation_required"
            };
            Assert.Equal(reason, Assert.Throws<IOException>(() => coordinator.VerifyAndPublish()).Message);
        }
        Assert.False(index.Current.IsReady);
        Assert.True(index.IsBlocked(new HashSet<string> { "configuration:other" }, out _));
        Assert.Equal("recovery_evidence_unverified", index.Current.GlobalBlockReason);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Theory(DisplayName = "Incoherent Persisted Recovery Facts Cannot Publish Admission Or Be Repaired During Identification")]
    [InlineData("missing-isolation")]
    [InlineData("foreign-isolation")]
    [InlineData("unknown-effect-digest")]
    [InlineData("terminal-unclosed-effect")]
    public void RejectsIncoherentFacts(string fault)
    {
        var fixture = Create();
        using (var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={fixture.Database.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$id", fixture.Registered.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$digest", new string('a', 64));
            command.Parameters.AddWithValue("$unknown", new string('z', 64));
            command.CommandText = fault switch
            {
                "missing-isolation" => "DELETE FROM main.resource_isolations WHERE journal_id=$id;",
                "foreign-isolation" => "INSERT INTO main.resource_isolations VALUES('unknown-resource',$id,'verified','unknown');",
                "unknown-effect-digest" => "INSERT INTO main.file_effects VALUES($id,'fixture-step','installCandidate','pending',$unknown,NULL);",
                _ => "INSERT INTO main.file_effects VALUES($id,'fixture-step','installCandidate','started',$digest,$digest); " +
                    "UPDATE main.operation_journals SET phase='committed',revision=2 WHERE journal_id=$id; DELETE FROM main.resource_isolations WHERE journal_id=$id;"
            };
            // Fund the deliberately corrupt row so this fixture reaches recovery-content validation.
            if (fault is "unknown-effect-digest" or "terminal-unclosed-effect")
                command.CommandText = "UPDATE main.recovery_capacity_reservations SET effect_limit=1,remaining_effects=1 WHERE owner_journal_id=$id;" + command.CommandText;
            command.ExecuteNonQuery();
        }
        using var observation = new DatabaseObservation(fixture.Database);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var index = new RecoveryIndex();
        var coordinator = new RecoveryCoordinator(fixture.Registry, new JournalRecoveryEvidence(fixture.Instance, fixture.Store), index);
        if (fault == "terminal-unclosed-effect") Assert.Throws<IOException>(() => coordinator.VerifyAndPublish());
        else
        {
            var reason = fault == "unknown-effect-digest" ? "recovery_registry_effect_invalid" : "recovery_registry_isolation_incoherent";
            Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => coordinator.VerifyAndPublish()).Message);
        }
        Assert.True(index.IsBlocked(new HashSet<string>(), out _));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    private static void AssertIsolation(RegisteredJournal journal, ResourceIsolation isolation)
    {
        Assert.Equal(journal.JournalId, isolation.JournalId);
        Assert.Equal(Assert.Single(journal.ResourceKeys), isolation.ResourceKey);
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
