using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RecoveryCapacityTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public void ReadsIndexedFamilies(int count)
    {
        var fixture = Create();
        fixture.Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$count) " +
                "INSERT INTO operations SELECT printf('00000000-0000-0000-0000-%012x',x),'indexed-history-fixture',$digest,'fixture.capacity','completed','fixture',NULL FROM n;" +
                "INSERT INTO recovery_capacity_reservations SELECT operation_id,1,0,1,1,0,1,0 FROM operations WHERE subject_id='indexed-history-fixture';" +
                "INSERT INTO recovery_capacity_members SELECT operation_id,operation_id FROM operations WHERE subject_id='indexed-history-fixture';" +
                "INSERT INTO operation_journals SELECT operation_id,operation_id,'fileCleanup','prepared',1,'blobs/plans/'||$digest||'.json',$digest,'fixture' FROM operations WHERE subject_id='indexed-history-fixture';" +
                "INSERT INTO journal_resources SELECT journal_id,'capacity:'||journal_id FROM operation_journals;UPDATE operation_journals SET phase='committed';";
            command.Parameters.AddWithValue("$count", count); command.Parameters.AddWithValue("$digest", new string('a', 64));
            command.ExecuteNonQuery(); return true;
        });
        _ = Seed(fixture, JournalKind.RunAttempt, new(1, 0, 1));
        var registry = new RecoveryJournalRegistry(fixture.Database);
        var timer = System.Diagnostics.Stopwatch.StartNew(); var observed = 0;
        registry.VisitCompleted(snapshot => { Assert.Single(snapshot.Journals); Assert.Empty(snapshot.FileEffects); observed++; });
        Assert.Equal(count, observed);
        Assert.Equal(count, registry.ReadInactiveIds().Count);
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
        using var explain = connection.CreateCommand(); explain.CommandText = "EXPLAIN QUERY PLAN " + RecoveryJournalRegistry.FamilyQuery;
        explain.Parameters.AddWithValue("$required", "00000000-0000-0000-0000-000000000001");
        var details = new List<string>(); using (var rows = explain.ExecuteReader()) while (rows.Read()) details.Add(rows.GetString(3));
        Assert.Contains(details, detail => detail.Contains("recovery_capacity_members_owner", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.StartsWith("SCAN j", StringComparison.Ordinal));
        using var version = connection.CreateCommand(); version.CommandText = "SELECT sqlite_version();";
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { completedFamilies = count, activeFamilies = 1,
            observedFamilyCallbacks = observed, metadataReadMilliseconds = timer.Elapsed.TotalMilliseconds, sqlite = version.ExecuteScalar(), queryPlan = details,
            scope = "Actual SQLite metadata fixture; no physical journal or full Host startup performance claim" }));
    }

    [Fact(DisplayName = "Completed History Beyond The Old Global Limit Does Not Consume Active Admission Capacity Or Lose Point Reads")]
    public void SeparatesCompletedHistory()
    {
        var fixture = Create();
        fixture.Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10001) " +
                "INSERT INTO operations SELECT printf('00000000-0000-0000-0000-%012x',x),'history-capacity-fixture',$digest,'fixture.capacity','completed','fixture',NULL FROM n;" +
                "INSERT INTO recovery_capacity_reservations SELECT operation_id,1,0,1,1,0,1,0 FROM operations WHERE subject_id='history-capacity-fixture';" +
                "INSERT INTO recovery_capacity_members SELECT operation_id,operation_id FROM operations WHERE subject_id='history-capacity-fixture';" +
                "INSERT INTO operation_journals SELECT operation_id,operation_id,'fileCleanup','prepared',1,'blobs/plans/'||$digest||'.json',$digest,'fixture' FROM operations WHERE subject_id='history-capacity-fixture';" +
                "INSERT INTO journal_resources SELECT journal_id,'capacity:'||journal_id FROM operation_journals;" +
                "UPDATE operation_journals SET phase='committed';";
            command.Parameters.AddWithValue("$digest", new string('a', 64)); command.ExecuteNonQuery();
            RecoveryCapacityStore.VerifyCounts(connection, transaction); return true;
        });
        var registry = new RecoveryJournalRegistry(fixture.Database);
        Assert.Empty(registry.ReadActiveSnapshot().Journals);
        Assert.Equal(10001, registry.ReadHistory().Count());
        var old = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Assert.Equal(JournalPhase.Committed, registry.ReadJournal(old).Phase);
        var active = Seed(fixture, JournalKind.RunAttempt, new(10000, 65536, 10000));
        Assert.Equal(active.JournalId, Assert.Single(registry.ReadActiveSnapshot().Journals).JournalId);
        Assert.Equal(old, Assert.Single(registry.ReadSnapshot(old).Journals).JournalId);
        Assert.Equal("recovery_capacity_exceeded", Assert.Throws<IOException>(() =>
            Accept(fixture.Database, Journal(JournalKind.RunAttempt), new(1, 0, 1))).Message);
        Assert.Equal(10002, registry.ReadHistory().Count());
        fixture.Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10001) " +
                "INSERT INTO operations SELECT printf('10000000-0000-0000-0000-%012x',x),'maintenance-history-fixture',$digest,'maintenance.update','completed','fixture',NULL FROM n;" +
                "INSERT INTO maintenance_operations(operation_id,plan_sha256,plan_size,source_sha256,source_size,state,terminal_code) " +
                "SELECT operation_id,$digest,1,$digest,1,3,'cancelled-before-handoff' FROM operations WHERE subject_id='maintenance-history-fixture';";
            command.Parameters.AddWithValue("$digest", new string('b', 64)); command.ExecuteNonQuery();
            Assert.Equal(10001, NexusPipeline.Platform.Storage.Maintenance.MaintenanceOperationRows.ReadAll(connection, transaction).Count());
            command.CommandText = "UPDATE operations SET status='accepted' WHERE operation_id=(SELECT MAX(operation_id) FROM maintenance_operations);";
            command.ExecuteNonQuery();
            Assert.Throws<IOException>(() => NexusPipeline.Platform.Storage.Maintenance.MaintenanceOperationRows.ReadAll(connection, transaction).ToArray());
            return true;
        });
    }

    [Theory(DisplayName = "Lifecycle Capacity Rejects Each Global Boundary Atomically Without Charging Replay")]
    [InlineData("journals")]
    [InlineData("effects")]
    [InlineData("jobs")]
    public void BoundsLifecycleAdmission(string full)
    {
        var fixture = Create();
        var budget = new RecoveryCapacityBudget(full == "journals" ? 10000 : 1,
            full == "effects" ? 65536 : 0, full == "jobs" ? 10000 : 1);
        var accepted = Seed(fixture, JournalKind.RunAttempt, budget);
        var before = Counts(fixture.Database);
        var rejected = Journal(JournalKind.RunAttempt);
        Assert.Equal("recovery_capacity_exceeded", Assert.Throws<IOException>(() => Accept(fixture.Database, rejected,
            new(1, 1, 1))).Message);
        Assert.Equal(before, Counts(fixture.Database));
        var replay = Accept(fixture.Database, accepted with { JournalId = Guid.NewGuid() }, new(1, 1, 1));
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(accepted.JournalId, replay.JournalId);
        Assert.Equal(before, Counts(fixture.Database));
    }

    [Fact(DisplayName = "Accepted Capacity Protects Deferred Children Jobs And Reverse Effects When Other Admissions Fill The Inventory")]
    public void ReservesDeferredChildren()
    {
        var fixture = Create();
        var parent = Seed(fixture, JournalKind.RunAttempt, new(2, 4, 2));
        var payload = Journal(JournalKind.UpdateApply);
        var forward = Effects(payload.JournalId, ["preserve-original", "install-candidate"]);
        _ = Accept(fixture.Database, payload, RecoveryCapacityBudget.ForJournal(payload, forward), forward);
        _ = Seed(fixture, JournalKind.RunAttempt, new(9997, 65528, 9995));
        Assert.Throws<IOException>(() => Accept(fixture.Database, Journal(JournalKind.RunAttempt), new(1, 0, 1)));

        var child = Journal(JournalKind.ConfigExchange);
        fixture.Database.Write((connection, transaction) =>
        {
            _ = OperationRows.Accept(connection, transaction, Identity(child), DateTimeOffset.UtcNow);
            RecoveryJournalRegistry.RegisterInside(connection, transaction, child,
                ConfigurationFileEffects.Create(child.JournalId, child.PlanDigest), JournalWriterStore.CurrentWriter,
                capacityParentJournalId: parent.JournalId);
            return true;
        });
        var job = "Local\\NexusWorker-" + Guid.NewGuid().ToString("N");
        foreach (var journal in new[] { parent, child })
            fixture.Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO journal_writer_jobs VALUES($journal,$writer,$job,0);";
                command.Parameters.AddWithValue("$journal", journal.JournalId.ToString("D"));
                command.Parameters.AddWithValue("$writer", JournalWriterStore.CurrentWriter.WriterId.ToString("D"));
                command.Parameters.AddWithValue("$job", job); command.ExecuteNonQuery(); return true;
            });
        fixture.Database.Write((connection, transaction) =>
        {
            FileEffectRows.Insert(connection, transaction, payload.JournalId,
                Effects(payload.JournalId, ["restore-retire-candidate", "restore-original"]));
            RecoveryCapacityStore.VerifyGlobal(connection, transaction);
            return true;
        });
        Assert.Equal(new long[] { 0, 0, 0 }, Remaining(fixture.Database, parent.JournalId));
        Assert.Equal(0, Remaining(fixture.Database, payload.JournalId)[1]);
        Assert.Throws<IOException>(() => Accept(fixture.Database, Journal(JournalKind.RunAttempt), new(1, 0, 1)));
    }

    [Fact(DisplayName = "Unused Capacity Releases Only After Every Member Commits And Retains Maintenance Restart Allowance")]
    public void ReleasesCommittedCapacity()
    {
        var fixture = Create();
        var parent = Seed(fixture, JournalKind.RunAttempt, new(3, 8, 5));
        var child = Journal(JournalKind.ConfigExchange);
        fixture.Database.Write((connection, transaction) =>
        {
            _ = OperationRows.Accept(connection, transaction, Identity(child), DateTimeOffset.UtcNow);
            RecoveryJournalRegistry.RegisterInside(connection, transaction, child, [], JournalWriterStore.CurrentWriter,
                capacityParentJournalId: parent.JournalId);
            return true;
        });
        CommitMetadata(fixture.Database, parent.JournalId);
        Assert.Equal(new long[] { 1, 8, 5 }, Remaining(fixture.Database, parent.JournalId));
        CommitMetadata(fixture.Database, child.JournalId);
        Assert.Equal(new long[] { 0, 0, 0 }, Remaining(fixture.Database, parent.JournalId));
        var maintenance = Seed(fixture, JournalKind.UpdateApply, new(1, 0, 3));
        CommitMetadata(fixture.Database, maintenance.JournalId);
        Assert.Equal(3, Remaining(fixture.Database, maintenance.JournalId)[2]);
    }

    [Theory(DisplayName = "Frozen Budget Validation Rejects Changed Allowances Even When Accounting Still Balances")]
    [InlineData("journal")]
    [InlineData("effect")]
    [InlineData("job")]
    public void RejectsChangedAllowances(string field)
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        _ = fixture.Service.Start(Guid.NewGuid(), "capacity-fixture", source.Request(new BindingTarget(source.BindingB)),
            PlanBuilderFixture.CreatedAt);
        RecoveryCapacityInventory.Verify(source.Instance, source.Database, new());
        var remaining = field switch { "journal" => "remaining_journals", "effect" => "remaining_effects", _ => "remaining_jobs" };
        fixture.Execute($"UPDATE recovery_capacity_reservations SET {field}_limit={field}_limit+1,{remaining}={remaining}+1;");
        Assert.Equal("recovery_capacity_inventory_invalid", Assert.Throws<IOException>(() =>
            RecoveryCapacityInventory.Verify(source.Instance, source.Database, new())).Message);
    }

    private static (RuntimeInstance Instance, StateDatabase Database) Create()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "capacity-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(path);
        return (instance, StateDatabase.CreateFresh(instance));
    }

    private static RegisteredJournal Journal(JournalKind kind)
    {
        var id = Guid.NewGuid(); var digest = new string('a', 64);
        return new(id, Guid.NewGuid(), kind, JournalPhase.Prepared, 1, $"blobs/plans/{digest}.json", digest, ["capacity:" + id.ToString("N")]);
    }

    private static OperationIdentity Identity(RegisteredJournal journal) => new(journal.OperationId, "capacity-fixture", journal.PlanDigest, "fixture.capacity");

    private static RegisteredJournal Seed((RuntimeInstance Instance, StateDatabase Database) fixture, JournalKind kind, RecoveryCapacityBudget budget)
    {
        var journal = Journal(kind); _ = Accept(fixture.Database, journal, budget); return journal;
    }

    private static (OperationAcceptance Acceptance, Guid JournalId) Accept(StateDatabase database, RegisteredJournal journal,
        RecoveryCapacityBudget budget, IReadOnlyList<FileEffect>? effects = null) => database.Write((connection, transaction) =>
            RecoveryJournalRegistry.AcceptPreparedInside(connection, transaction, Identity(journal), journal, effects ?? [],
                DateTimeOffset.UtcNow, null, JournalWriterStore.CurrentWriter, budget));

    private static FileEffect[] Effects(Guid journalId, string[] names) => names.Select(name => new FileEffect(journalId, name,
        name.Contains("retire", StringComparison.Ordinal) ? FileEffectAction.RetireCandidate :
        name.StartsWith("restore", StringComparison.Ordinal) ? FileEffectAction.RestoreOriginal :
        name.StartsWith("preserve", StringComparison.Ordinal) ? FileEffectAction.PreserveOriginal : FileEffectAction.InstallCandidate,
        FileEffectState.Pending, new string('a', 64), null)).ToArray();

    private static long[] Counts(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM operations),(SELECT count(*) FROM operation_journals)," +
            "(SELECT count(*) FROM recovery_capacity_members),(SELECT count(*) FROM recovery_capacity_reservations);";
        using var row = command.ExecuteReader(); Assert.True(row.Read());
        return Enumerable.Range(0, 4).Select(row.GetInt64).ToArray();
    }

    private static long[] Remaining(StateDatabase database, Guid owner)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT remaining_journals,remaining_effects,remaining_jobs FROM recovery_capacity_reservations WHERE owner_journal_id=$owner;";
        command.Parameters.AddWithValue("$owner", owner.ToString("D"));
        using var row = command.ExecuteReader(); Assert.True(row.Read());
        return Enumerable.Range(0, 3).Select(row.GetInt64).ToArray();
    }

    private static void CommitMetadata(StateDatabase database, Guid journal) => database.Write((connection, transaction) =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE operation_journals SET phase='committed' WHERE journal_id=$id;";
        command.Parameters.AddWithValue("$id", journal.ToString("D")); command.ExecuteNonQuery(); return true;
    });
}
