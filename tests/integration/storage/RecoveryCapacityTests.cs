using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RecoveryCapacityTests
{
    private static readonly RecoveryCapacityBudget MaximumBudget = new(RecoveryCapacityBudget.MaximumJournals,
        RecoveryCapacityBudget.MaximumFileEffects, RecoveryCapacityBudget.MaximumJobs);

    [Fact(DisplayName = "Completed Family Reads Return Every Original Journal Without Including Active Work")]
    public void ReadsCompletedFamilies()
    {
        var fixture = Create();
        var completed = SeedHistory(fixture.Database, 1000);
        var active = Seed(fixture, JournalKind.RunAttempt, new(1, 0, 1));
        var registry = new RecoveryJournalRegistry(fixture.Database);
        var observed = registry.ReadCompleted().Select(snapshot =>
        {
            var journal = Assert.Single(snapshot.Journals);
            Assert.Equal(journal.JournalId, journal.OperationId);
            Assert.Equal(JournalPhase.Committed, journal.Phase);
            Assert.Equal("capacity:" + journal.JournalId.ToString("D"), Assert.Single(journal.ResourceKeys));
            Assert.Empty(snapshot.FileEffects);
            Assert.Empty(snapshot.Isolations);
            return journal.JournalId;
        });
        Assert.Equal(completed.Order(), observed.Order());
        Assert.Equal(completed.Order(), registry.ReadInactiveIds().Order());
        Assert.Equal(active.JournalId, Assert.Single(registry.ReadActiveSnapshot().Journals).JournalId);
        fixture.Database.Write((connection, transaction) =>
        {
            using var change = connection.CreateCommand(); change.Transaction = transaction;
            change.CommandText = "UPDATE main.journal_resources SET resource_key=' ' WHERE journal_id=$id;";
            change.Parameters.AddWithValue("$id", completed.Order().Last().ToString("D")); change.ExecuteNonQuery();
            return true;
        });
        Assert.Equal("recovery_registry_journal_invalid", Assert.Throws<IOException>(() => registry.ReadInactiveIds()).Message);
    }

    [Fact(DisplayName = "Completed History Beyond The Old Global Limit Does Not Consume Active Admission Capacity Or Lose Point Reads")]
    public void SeparatesCompletedHistory()
    {
        var fixture = Create();
        var completed = SeedHistory(fixture.Database, RecoveryCapacityBudget.MaximumJournals + 1);
        var registry = new RecoveryJournalRegistry(fixture.Database);
        var old = completed[0];
        var historical = registry.ReadJournal(old);
        Assert.Equal(JournalPhase.Committed, historical.Phase);
        var active = Seed(fixture, JournalKind.RunAttempt, MaximumBudget);
        Assert.Equal(active.JournalId, Assert.Single(registry.ReadActiveSnapshot().Journals).JournalId);
        var readBack = Assert.Single(registry.ReadSnapshot(old).Journals);
        Assert.Equal(historical.OperationId, readBack.OperationId);
        Assert.Equal(historical.JournalId, readBack.JournalId);
        Assert.Equal(historical.ResourceKeys, readBack.ResourceKeys);
        AssertCapacityRefusal(fixture.Database, Journal(JournalKind.RunAttempt), new(1, 0, 1));
        Assert.Equal(completed.Append(active.JournalId).Order(), registry.ReadHistory().Select(item => item.JournalId).Order());
        VerifyMaintenanceHistory(fixture.Database);
    }

    private static void VerifyMaintenanceHistory(StateDatabase database)
    {
        database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10001) " +
                "INSERT INTO main.operations SELECT printf('10000000-0000-0000-0000-%012x',x),'maintenance-history-fixture',$digest,'maintenance.update','completed','fixture',NULL FROM n;" +
                "INSERT INTO system.maintenance_operations(operation_id,plan_sha256,plan_size,source_sha256,source_size,state,terminal_code) " +
                "SELECT operation_id,$digest,1,$digest,1,3,'cancelled-before-handoff' FROM main.operations WHERE subject_id='maintenance-history-fixture';";
            command.Parameters.AddWithValue("$digest", new string('b', 64)); command.ExecuteNonQuery();
            var expected = Enumerable.Range(1, 10001).Select(index => Guid.Parse($"10000000-0000-0000-0000-{index:x12}"));
            var history = MaintenanceOperationRows.ReadAll(connection, transaction).ToArray();
            Assert.Equal(expected.Order(), history.Select(item => item.Identity.OperationId).Order());
            Assert.All(history, item => Assert.Equal("cancelled-before-handoff", item.TerminalCode));
            command.CommandText = "UPDATE main.operations SET status='accepted' WHERE operation_id=$last;";
            command.Parameters.AddWithValue("$last", expected.Last().ToString("D"));
            command.ExecuteNonQuery();
            Assert.Equal("maintenance_operation_unbound", Assert.Throws<IOException>(() => MaintenanceOperationRows.ReadAll(connection, transaction).ToArray()).Message);
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
        var budget = new RecoveryCapacityBudget(full == "journals" ? MaximumBudget.Journals : 1,
            full == "effects" ? MaximumBudget.FileEffects : 0, full == "jobs" ? MaximumBudget.Jobs : 1);
        var accepted = Seed(fixture, JournalKind.RunAttempt, budget);
        var registry = new RecoveryJournalRegistry(fixture.Database);
        var before = registry.ReadActiveSnapshot();
        AssertCapacityRefusal(fixture.Database, Journal(JournalKind.RunAttempt), new(1, 1, 1));
        var replacement = accepted with { JournalId = Guid.NewGuid() };
        var replay = Accept(fixture.Database, replacement, new(1, 1, 1));
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(accepted.JournalId, replay.JournalId);
        Assert.Equal(accepted.OperationId, replay.Acceptance.OperationId);
        Assert.Null(registry.FindJournal(replacement.JournalId));
        var after = registry.ReadActiveSnapshot();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Isolations, after.Isolations);
        Assert.Equal(accepted.JournalId, Assert.Single(after.Journals).JournalId);
    }

    [Fact(DisplayName = "Accepted Capacity Protects Deferred Children Jobs And Reverse Effects When Other Admissions Fill The Inventory")]
    public void ReservesDeferredChildren()
    {
        var source = ExecutionPlanFixture.CreateManagedPaths();
        var fixture = (source.Instance, source.Database);
        var first = source.Plan.Steps[0] with { MaximumAttempts = 3 };
        var second = first with { StepId = "second", MaximumAttempts = 2 };
        var slot = source.Plan.ConfigurationSlots[0];
        var plan = source.Plan with
        {
            Steps = [first, second],
            ConfigurationSlots = [slot, slot with { ResourceId = "other", TargetPath = slot.TargetPath + "-other" }, slot with { StepId = second.StepId }]
        };
        var budget = ExecutionRecoveryCapacity.Measure(plan);
        var parent = Seed(fixture, JournalKind.RunAttempt, budget);
        var payload = Journal(JournalKind.UpdateApply);
        var forward = Effects(payload.JournalId, ["preserve-original", "install-candidate"]);
        var payloadBudget = RecoveryCapacityBudget.ForJournal(payload, forward);
        _ = Accept(fixture.Database, payload, payloadBudget, forward);
        FillCapacity(fixture, budget, payloadBudget);
        AssertCapacityRefusal(fixture.Database, Journal(JournalKind.RunAttempt), new(1, 0, 1));

        var family = new List<Guid> { parent.JournalId };
        var outbox = new FileEffectOutbox(fixture.Database);
        foreach (var step in plan.Steps)
            for (var invocation = 0; invocation < (step.MaximumAttempts ?? 1); invocation++)
            {
                var owners = new List<Guid> { parent.JournalId };
                foreach (var _ in plan.ConfigurationSlots.Where(item => item.StepId == step.StepId))
                {
                    var child = Journal(JournalKind.ConfigExchange);
                    var effects = ConfigurationFileEffects.Create(child.JournalId, child.PlanDigest);
                    RegisterChild(fixture.Database, parent, child, effects);
                    Assert.Equal(effects.OrderBy(item => item.StepId, StringComparer.Ordinal),
                        outbox.Read(child.JournalId).OrderBy(item => item.StepId, StringComparer.Ordinal));
                    owners.Add(child.JournalId);
                    family.Add(child.JournalId);
                }
                using var job = new OwnedWorkerJob();
                new JournalWriterStore(fixture.Database).RegisterJob(owners, job);
                AssertJobOwners(fixture.Database, job.Name, owners);
            }
        var reverse = Effects(payload.JournalId, ["restore-retire-candidate", "restore-original"]);
        fixture.Database.Write((connection, transaction) =>
        {
            FileEffectRows.Insert(connection, transaction, payload.JournalId, reverse);
            return true;
        });
        Assert.Equal(forward.Concat(reverse).OrderBy(item => item.StepId, StringComparer.Ordinal),
            outbox.Read(payload.JournalId).OrderBy(item => item.StepId, StringComparer.Ordinal));
        var saved = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot(parent.JournalId);
        Assert.Equal(family.Order(), saved.Journals.Select(item => item.JournalId).Order());
        AssertCapacityRefusal(fixture.Database, Journal(JournalKind.RunAttempt), new(1, 0, 1));
    }

    [Fact(DisplayName = "Unused Capacity Releases Only After Every Member Commits And Retains Maintenance Restart Allowance")]
    public void ReleasesCommittedCapacity()
    {
        var fixture = Create();
        var familyBudget = new RecoveryCapacityBudget(3, 8, 5);
        var maintenanceBudget = new RecoveryCapacityBudget(1, 0, 3);
        var spareBudget = new RecoveryCapacityBudget(1, 0, 0);
        var parent = Seed(fixture, JournalKind.RunAttempt, familyBudget);
        var child = Journal(JournalKind.ConfigExchange);
        RegisterChild(fixture.Database, parent, child, []);
        var maintenance = Seed(fixture, JournalKind.UpdateApply, maintenanceBudget);
        var spare = Seed(fixture, JournalKind.FileCleanup, spareBudget);
        FillCapacity(fixture, familyBudget, maintenanceBudget, spareBudget);
        CommitMetadata(fixture.Database, parent.JournalId);
        AssertCapacityRefusal(fixture.Database, Journal(JournalKind.RunAttempt), new(1, 0, 1));
        CommitMetadata(fixture.Database, child.JournalId);
        var replacement = Seed(fixture, JournalKind.RunAttempt, familyBudget);
        CommitMetadata(fixture.Database, spare.JournalId);
        CommitMetadata(fixture.Database, maintenance.JournalId);
        AssertCapacityRefusal(fixture.Database, Journal(JournalKind.RunAttempt), new(1, 0, 1));
        var withoutJob = Seed(fixture, JournalKind.FileCleanup, spareBudget);
        var registry = new RecoveryJournalRegistry(fixture.Database);
        Assert.Equal(replacement.JournalId, registry.ReadOperationJournal(replacement.OperationId).JournalId);
        Assert.Equal(withoutJob.JournalId, registry.ReadOperationJournal(withoutJob.OperationId).JournalId);
    }

    [Theory(DisplayName = "Frozen Budget Validation Rejects Changed Allowances Even When Accounting Still Balances")]
    [InlineData("journal")]
    [InlineData("effect")]
    [InlineData("job")]
    public void RejectsChangedAllowances(string field)
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        var accepted = fixture.Service.Start(Guid.NewGuid(), "capacity-fixture", source.Request(new BindingTarget(source.BindingB)),
            PlanBuilderFixture.CreatedAt);
        RecoveryCapacityInventory.Verify(source.Instance, source.Database, new());
        var remaining = field switch { "journal" => "remaining_journals", "effect" => "remaining_effects", _ => "remaining_jobs" };
        source.Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"UPDATE main.recovery_capacity_reservations SET {field}_limit={field}_limit+1,{remaining}={remaining}+1 WHERE owner_journal_id=$owner;";
            command.Parameters.AddWithValue("$owner", accepted.JournalId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
            return true;
        });
        Assert.Equal("recovery_capacity_inventory_invalid", Assert.Throws<IOException>(() =>
            RecoveryCapacityInventory.Verify(source.Instance, source.Database, new())).Message);
        var original = new RecoveryJournalRegistry(source.Database).ReadOperationJournal(accepted.Acceptance.OperationId);
        Assert.Equal(accepted.JournalId, original.JournalId);
        Assert.Equal(accepted.Plan.Sha256, original.PlanDigest);
        Assert.Equal(JournalPhase.Prepared, original.Phase);
    }

    private static (RuntimeInstance Instance, StateDatabase Database) Create()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "capacity-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(path);
        return (instance, StateDatabase.CreateFresh(instance));
    }

    private static Guid[] SeedHistory(StateDatabase database, int count)
    {
        database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$count) " +
                "INSERT INTO main.operations SELECT printf('00000000-0000-0000-0000-%012x',x),'history-capacity-fixture',$digest,'fixture.capacity','completed','fixture',NULL FROM n;" +
                "INSERT INTO main.recovery_capacity_reservations SELECT operation_id,1,0,1,1,0,1,0 FROM main.operations WHERE subject_id='history-capacity-fixture';" +
                "INSERT INTO main.recovery_capacity_members SELECT operation_id,operation_id FROM main.operations WHERE subject_id='history-capacity-fixture';" +
                "INSERT INTO main.operation_journals SELECT operation_id,operation_id,'fileCleanup','prepared',1,'blobs/plans/'||$digest||'.json',$digest,'fixture' FROM main.operations WHERE subject_id='history-capacity-fixture';" +
                "INSERT INTO main.journal_resources SELECT journal_id,'capacity:'||journal_id FROM main.operation_journals;UPDATE main.operation_journals SET phase='committed';";
            command.Parameters.AddWithValue("$count", count); command.Parameters.AddWithValue("$digest", new string('a', 64));
            command.ExecuteNonQuery();
            return true;
        });
        return Enumerable.Range(1, count).Select(index => Guid.Parse($"00000000-0000-0000-0000-{index:x12}")).ToArray();
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

    private static void FillCapacity((RuntimeInstance Instance, StateDatabase Database) fixture, params RecoveryCapacityBudget[] occupied)
    {
        var available = new RecoveryCapacityBudget(MaximumBudget.Journals - occupied.Sum(item => item.Journals),
            MaximumBudget.FileEffects - occupied.Sum(item => item.FileEffects), MaximumBudget.Jobs - occupied.Sum(item => item.Jobs));
        _ = Seed(fixture, JournalKind.RunAttempt, available);
    }

    private static void RegisterChild(StateDatabase database, RegisteredJournal parent, RegisteredJournal child, IReadOnlyList<FileEffect> effects) =>
        database.Write((connection, transaction) =>
        {
            _ = OperationRows.Accept(connection, transaction, Identity(child), DateTimeOffset.UtcNow);
            RecoveryJournalRegistry.RegisterInside(connection, transaction, child, effects, JournalWriterStore.CurrentWriter,
                capacityParentJournalId: parent.JournalId);
            return true;
        });

    private static FileEffect[] Effects(Guid journalId, string[] names) => names.Select(name => new FileEffect(journalId, name,
        name.Contains("retire", StringComparison.Ordinal) ? FileEffectAction.RetireCandidate :
        name.StartsWith("restore", StringComparison.Ordinal) ? FileEffectAction.RestoreOriginal :
        name.StartsWith("preserve", StringComparison.Ordinal) ? FileEffectAction.PreserveOriginal : FileEffectAction.InstallCandidate,
        FileEffectState.Pending, new string('a', 64), null)).ToArray();

    private static void AssertCapacityRefusal(StateDatabase database, RegisteredJournal journal, RecoveryCapacityBudget budget)
    {
        Assert.Equal("recovery_capacity_exceeded", Assert.Throws<IOException>(() => Accept(database, journal, budget)).Message);
        Assert.Null(new RecoveryJournalRegistry(database).FindJournal(journal.JournalId));
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Null(OperationRows.ReadExisting(connection, transaction, Identity(journal)));
    }

    private static void AssertJobOwners(StateDatabase database, string name, IReadOnlyList<Guid> expected)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT journal_id FROM main.journal_writer_jobs WHERE job_name=$name AND writer_id=$writer;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$writer", JournalWriterStore.CurrentWriter.WriterId.ToString("D"));
        using var rows = command.ExecuteReader();
        var actual = new List<Guid>();
        while (rows.Read()) actual.Add(Guid.Parse(rows.GetString(0)));
        Assert.Equal(expected.Order(), actual.Order());
    }

    private static void CommitMetadata(StateDatabase database, Guid journal) => database.Write((connection, transaction) =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE main.operation_journals SET phase='committed' WHERE journal_id=$id;";
        command.Parameters.AddWithValue("$id", journal.ToString("D")); command.ExecuteNonQuery(); return true;
    });
}
