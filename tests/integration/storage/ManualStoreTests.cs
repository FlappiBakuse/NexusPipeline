using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ManualStoreTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Manual Admission Bounds Warm Capacity Work While Counting Every Run Without A Result")]
    public async Task BoundsCapacityWithCompletedHistory()
    {
        using var fixture = new Fixture();
        var observed = await fixture.ObserveAsync();
        fixture.Catalog.Lease.BindDatabase(fixture.Catalog.Database);
        var path = fixture.Catalog.Database.DatabasePath;
        using (var seedWriter = DatabaseConnections.Open(path))
        using (var transaction = seedWriter.BeginTransaction(deferred: false))
        {
            using var seed = DatabaseConnections.Command(seedWriter, transaction, """
                WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<12000)
                INSERT INTO main.operations(operation_id,subject_id,request_digest,operation_kind,status,created_at)
                SELECT printf('9c8483bc-98b1-41cb-abd5-%012d',n),'fixture:history',printf('%064d',n),
                       'manual.work-run','completed','2026-01-01T00:00:00Z' FROM numbers;
                INSERT INTO main.manual_work_runs
                SELECT operation_id,'fixture','tasks',$0,'blobs/plans/fixture.json',1,request_digest
                FROM main.operations WHERE subject_id='fixture:history';
                INSERT INTO main.manual_work_result
                SELECT operation_id,relative_path,size_bytes,sha256 FROM main.manual_work_runs;
                """, fixture.TaskId.ToString("D"));
            seed.ExecuteNonQuery();
            transaction.Commit();
        }

        using (var warm = Accept(Guid.NewGuid()))
            Assert.NotNull(fixture.Store.Complete(warm, null, "fixture_completed").Result);

        var batches = 0;
        var acceptedOnMeasuredConnection = false;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
            acceptedOnMeasuredConnection |= sql.StartsWith("INSERT INTO main.manual_work_runs ", StringComparison.Ordinal);
        SQLitePCL.sqlite3 writerHandle;
        using (var writer = DatabaseConnections.Open(path))
        {
            writerHandle = writer.Handle!;
            SQLitePCL.raw.sqlite3_progress_handler(writerHandle, 100, _ => { batches++; return 0; }, null);
            SQLitePCL.raw.sqlite3_trace(writerHandle, trace, null);
        }
        AcceptedManualWork measured;
        try { measured = Accept(Guid.NewGuid()); }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(writerHandle, (SQLitePCL.strdelegate_trace?)null, null);
            SQLitePCL.raw.sqlite3_progress_handler(writerHandle, 0, null, null);
        }
        Assert.True(acceptedOnMeasuredConnection);
        using (measured)
            Assert.NotNull(fixture.Store.Complete(measured, null, "fixture_completed").Result);

        using (var external = DatabaseConnections.Open(path, allowPooling: false, domains: DatabaseDomain.Runtime))
        using (var delete = DatabaseConnections.Command(external, null,
            "DELETE FROM main.manual_work_result WHERE operation_id BETWEEN $0 AND $1;",
            "9c8483bc-98b1-41cb-abd5-000000000001", "9c8483bc-98b1-41cb-abd5-000000004095"))
            Assert.Equal(4095, delete.ExecuteNonQuery());

        var rejected = Guid.NewGuid();
        using (var last = Accept(Guid.NewGuid()))
        {
            Assert.Equal("manual_work_capacity", Assert.Throws<IOException>(() => Accept(rejected)).Message);
            Assert.Null(fixture.Store.Lookup(rejected, "fixture:caller", "fixture", "tasks", fixture.TaskId, 7));
            using (var writer = DatabaseConnections.Open(path))
            using (var transaction = writer.BeginTransaction(deferred: false))
            {
                using var delete = DatabaseConnections.Command(writer, transaction,
                    "DELETE FROM main.manual_work_result WHERE operation_id=$0;", "9c8483bc-98b1-41cb-abd5-000000004096");
                Assert.Equal(1, delete.ExecuteNonQuery());
                transaction.Rollback();
            }
            Assert.NotNull(fixture.Store.Complete(last, null, "fixture_completed").Result);
        }
        using var retry = Accept(rejected);
        Assert.Equal(rejected, retry.Source.Document.OperationId);
        output.WriteLine($"manual admission: 12000 historical runs, {batches} SQLite progress callbacks at 100 VM instructions");
        Assert.InRange(batches, 0, 20);

        AcceptedManualWork Accept(Guid operation)
        {
            var started = DateTimeOffset.UtcNow;
            return fixture.Store.Accept(observed, operation, "fixture:caller", Guid.NewGuid(), started,
                fixture.Budget(started), () => { });
        }
    }

    [Fact(DisplayName = "Manual Work Reads Its Saved Source And Operation Once Per Snapshot")]
    public async Task ReadsBoundRowsOnce()
    {
        using var fixture = new Fixture();
        var observed = await fixture.ObserveAsync();
        var operation = Guid.NewGuid();
        var id = operation.ToString("D");
        var started = DateTimeOffset.UtcNow;
        var instance = fixture.Catalog.Instance;
        var database = fixture.Catalog.Database;
        using var work = fixture.Store.Accept(observed, operation, "fixture:caller", Guid.NewGuid(),
            started, fixture.Budget(started), () => { });
        fixture.Store.Begin(work);
        var pending = fixture.Store.Read(work);
        Assert.NotNull(pending.Begin);
        Assert.Null(pending.Result);

        using (var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime))
        using (var transaction = connection.BeginTransaction())
        {
            using var digest = DatabaseConnections.Command(connection, transaction,
                "UPDATE main.operations SET request_digest=$0 WHERE operation_id=$1;", new string('0', 64), id);
            Assert.Equal(1, digest.ExecuteNonQuery());
            Assert.Throws<OperationConflictException>(() => ManualWorkRows.Read(instance, new(), connection, transaction, work.Source.Reference));
            using var metadata = DatabaseConnections.Command(connection, transaction,
                "UPDATE main.manual_work_runs SET provider_id=$0 WHERE operation_id=$1;", "different", id);
            Assert.Equal(1, metadata.ExecuteNonQuery());
            Assert.Equal("manual_work_evidence_unbound", Assert.Throws<IOException>(() =>
                ManualWorkRows.Read(instance, new(), connection, transaction, work.Source.Reference)).Message);
            transaction.Rollback();
        }

        var accepted = Inspect("accepted", pending);
        var completed = fixture.Store.Complete(work, new(BusinessOutcome.Succeeded, "fixture_succeeded"), "fixture_returned");
        Assert.Equal("returned", completed.Result!.Completion);
        Assert.Equal(pending.Begin, completed.Begin);
        var finished = Inspect("completed", completed);
        Assert.All(new[] { accepted, finished }, statements =>
        {
            Assert.Single(statements, sql => sql.Contains(" FROM main.manual_work_runs WHERE ", StringComparison.Ordinal));
            Assert.Single(statements, sql => sql.Contains(" FROM main.operations WHERE ", StringComparison.Ordinal));
        });

        List<string> Inspect(string status, ManualWorkState expected)
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, allowPooling: false,
                domains: DatabaseDomain.Runtime);
            using var transaction = DatabaseConnections.BeginRead(connection);
            var contracts = new ContractReader();
            ManualWorkState Read() => ManualWorkRows.Read(instance, contracts, connection, transaction, work.Source.Reference);
            var statements = new List<string>();
            SQLitePCL.strdelegate_trace trace = (_, sql) =>
            {
                if (sql.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)) statements.Add(sql);
            };
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
            ManualWorkState actual;
            try { actual = Read(); }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace?)null, null); }
            Assert.Equal(expected.Source.Reference, actual.Source.Reference);
            Assert.True(expected.Source.Document.Invocation.Matches(actual.Source.Document.Invocation));
            Assert.Equal(expected.BeginReference, actual.BeginReference);
            Assert.Equal(expected.Begin, actual.Begin);
            Assert.Equal(expected.ResultReference, actual.ResultReference);
            Assert.Equal(expected.Result, actual.Result);

            const int iterations = 120;
            for (var i = 0; i < 10; i++) _ = Read();
            using var process = Process.GetCurrentProcess();
            var cpuStarted = process.TotalProcessorTime;
            var allocatedStarted = GC.GetAllocatedBytesForCurrentThread();
            var startedAt = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++) _ = Read();
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStarted;
            var cpu = process.TotalProcessorTime - cpuStarted;
            output.WriteLine($"manual-work state={status} selects={statements.Count} repetitions={iterations} elapsed-ms={elapsed.TotalMilliseconds:F3} cpu-ms={cpu.TotalMilliseconds:F3} allocated-bytes={allocated}");
            foreach (var sql in statements.Distinct(StringComparer.Ordinal))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                if (sql.Contains("$0", StringComparison.Ordinal)) command.Parameters.AddWithValue("$0", id);
                if (sql.Contains("$id", StringComparison.Ordinal)) command.Parameters.AddWithValue("$id", id);
                using var planner = command.ExecuteReader();
                while (planner.Read()) output.WriteLine(planner.GetString(3));
            }
            return statements;
        }
    }

    [Theory(DisplayName = "Manual Acceptance Persists Its Actual Caller Task And Run Without A Schedule And Never Rewinds An Original Receipt")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistsBoundFacts(bool begin)
    {
        using var fixture = new Fixture(); var observed = await fixture.ObserveAsync();
        var operation = Guid.NewGuid(); var client = Guid.NewGuid(); var started = DateTimeOffset.UtcNow;
        using var work = fixture.Store.Accept(observed, operation, "fixture:caller", client, started,
            fixture.Budget(started), () => { });
        var state = fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 7)!;
        Assert.Equal(client, state.Source.Document.ClientId); Assert.Empty(state.Source.Document.Task.ScheduledTaskIds);
        Assert.Empty(fixture.Registry.PublishedPlanningEntries());
        Assert.True(work.Source.Document.Invocation.Matches(state.Source.Document.Invocation));
        Assert.Throws<OperationConflictException>(() => fixture.Store.Lookup(operation, "foreign", "fixture", "tasks", fixture.TaskId, 7));
        Assert.Throws<OperationConflictException>(() => fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 8));
        Assert.False(fixture.Store.ReconcilePriorOwners());
        var before = File.ReadAllBytes(fixture.Catalog.Database.DatabasePath);
        Assert.Equal("manual_work_owner_unconfirmed", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Catalog.Instance,
            Path.Combine(fixture.Catalog.Instance.RuntimeRoot, "live-inspection"), new JournalStartupAdmission(fixture.Catalog.Instance, new()))).Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.Catalog.Database.DatabasePath));
        if (begin)
        {
            fixture.Store.Begin(work);
            Assert.Equal("manual_work_already_dispatched", Assert.Throws<IOException>(() => fixture.Store.Begin(work)).Message);
        }
        var result = fixture.Store.Complete(work, new(BusinessOutcome.Succeeded, "fixture_succeeded"), "fixture_returned");
        Assert.Equal(begin ? "returned" : "notStarted", result.Result!.Completion);
        Assert.Equal(begin ? BusinessOutcome.Succeeded : (BusinessOutcome?)null, result.Result.Returned?.Business);
        var replay = fixture.Store.Complete(work, new(BusinessOutcome.Failed, "fixture_other"), "fixture_other");
        Assert.Equal(result.Result, replay.Result); Assert.Equal(result.ResultReference, replay.ResultReference);
        Assert.True(fixture.Store.ReconcilePriorOwners());
        ManualWorkInventory.Visit(fixture.Catalog.Instance, fixture.Catalog.Database, new(), saved => Assert.Equal(result.ResultReference, saved.ResultReference));
        _ = StateDatabase.OpenExisting(fixture.Catalog.Instance, Path.Combine(fixture.Catalog.Instance.RuntimeRoot, "completed-inspection"),
            new JournalStartupAdmission(fixture.Catalog.Instance, new()));
        Assert.Equal(0, fixture.Source.Calls);
    }

    [Fact(DisplayName = "Revocation At The Manual Commit Boundary Leaves No Accepted Operation Or Run")]
    public async Task RejectsRevokedAcceptance()
    {
        using var fixture = new Fixture(); var observed = await fixture.ObserveAsync(); var operation = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var lifecycle = fixture.Catalog.Lifecycle;
        var before = lifecycle.ReadCompletionActivity().Runs;
        var revoked = Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Accept(observed, operation, "fixture:caller",
            Guid.NewGuid(), started, fixture.Budget(started), () =>
            {
                var runs = lifecycle.ReadCompletionActivity().Runs;
                if (runs == before) return;
                Assert.Equal(before + 1, runs);
                throw new UnauthorizedAccessException("fixture_revoked");
            }));
        Assert.Equal("fixture_revoked", revoked.Message);
        Assert.Equal(before, lifecycle.ReadCompletionActivity().Runs);
        Assert.Null(fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 7));
        Assert.NotNull(fixture.Catalog.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        var count = 0;
        ManualWorkInventory.Visit(fixture.Catalog.Instance, fixture.Catalog.Database, new(), _ => count++);
        Assert.Equal(0, count); Assert.Equal(0, fixture.Source.Calls);
    }

    [Fact(DisplayName = "A Task Quiescence Hold Rejects New Durable Manual Acceptance Before Any Run Or Callback Exists")]
    public async Task QuiescesBeforeAcceptance()
    {
        using var fixture = new Fixture(); var observed = await fixture.ObserveAsync(); var operation = Guid.NewGuid();
        using var hold = await observed.Entry.QuiesceAsync([fixture.TaskId]); var started = DateTimeOffset.UtcNow;
        Assert.Equal("work_task_quiescing", Assert.Throws<InvalidOperationException>(() => fixture.Store.Accept(observed, operation,
            "fixture:caller", Guid.NewGuid(), started, fixture.Budget(started), () => { })).Message);
        Assert.Null(fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 7));
        Assert.Equal(0, fixture.Source.Calls);
    }

    [Theory(DisplayName = "A Rebound Immutable Manual Source Cannot Replace The Accepted Writer Or Client Facts Even When Its Business Request Is Identical")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsReboundSource(bool completed)
    {
        using var fixture = new Fixture(); var observed = await fixture.ObserveAsync(); var operation = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        using var work = fixture.Store.Accept(observed, operation, "fixture:caller", Guid.NewGuid(), started, fixture.Budget(started), () => { });
        if (completed)
            Assert.NotNull(fixture.Store.Complete(work, new(BusinessOutcome.Succeeded, "fixture_succeeded"), "fixture_returned").Result);
        var changed = work.Source.Document with { ClientId = Guid.NewGuid() };
        var replacement = new ManualWorkCodec(fixture.Catalog.Instance, new()).Persist(changed);
        fixture.Catalog.Database.Write((connection, transaction) =>
        {
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE main.manual_work_runs SET relative_path=$path,size_bytes=$size,sha256=$sha WHERE operation_id=$id;";
            update.Parameters.AddWithValue("$path", replacement.RelativePath); update.Parameters.AddWithValue("$size", replacement.SizeBytes);
            update.Parameters.AddWithValue("$sha", replacement.Sha256); update.Parameters.AddWithValue("$id", operation.ToString("D"));
            return update.ExecuteNonQuery();
        });
        var before = File.ReadAllBytes(fixture.Catalog.Database.DatabasePath);
        Assert.Throws<OperationConflictException>(() => fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 7));
        Assert.Throws<OperationConflictException>(() => StateDatabase.OpenExisting(fixture.Catalog.Instance,
            Path.Combine(fixture.Catalog.Instance.RuntimeRoot, "rebound-inspection"), new JournalStartupAdmission(fixture.Catalog.Instance, new())));
        var catalog = fixture.Catalog;
        var execution = new ExecutionAdmissionService(catalog.Instance, catalog.Database, new(), catalog.Lease, catalog.Resources,
            catalog.Index, new(), new(5, 6, 7));
        Assert.Throws<OperationConflictException>(() => new PersistentScheduleService(catalog.Instance, catalog.Database, catalog.Lease,
            catalog.Resources, catalog.Lifecycle, catalog.Index, execution));
        Assert.Equal(before, File.ReadAllBytes(fixture.Catalog.Database.DatabasePath));
        Assert.Equal(0, fixture.Source.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        internal ExtensionCatalogFixture Catalog { get; } = new();
        internal Source Source { get; } = new();
        internal Guid TaskId => Source.TaskId;
        internal ScheduledWorkRegistry Registry { get; }
        internal ManualWorkStore Store { get; }
        private readonly IDisposable _registration;
        internal Fixture()
        {
            Registry = new(new Policy()); var activation = new ExtensionActivation("fixture"); activation.Publish();
            _registration = Registry.Register(activation, "tasks", Source);
            Store = new(Catalog.Instance, Catalog.Database, Catalog.Lease, Catalog.Resources, new(), TimeProvider.System);
        }
        internal async Task<ObservedManualTask> ObserveAsync() => (await Registry.RequireManualEntry("fixture", "tasks").ReadManualAsync(TaskId))!;
        internal ExecutionTimeBudget Budget(DateTimeOffset started) => new(TimeProvider.System, TimeProvider.System.GetTimestamp(), TimeSpan.FromMinutes(1), started.AddMinutes(1));
        public void Dispose() { _registration.Dispose(); Catalog.Dispose(); }
    }
    private sealed class Policy : IScheduledInvocationAuthorization
    {
        public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId) => throw new UnauthorizedAccessException();
    }
    private sealed class Source : IManualWorkSource
    {
        internal Guid TaskId { get; } = Guid.NewGuid(); internal int Calls;
        public ValueTask<ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellation) =>
            ValueTask.FromResult<ManualWorkTask?>(taskId == TaskId ? new(TaskId, 7, true, "UTC", 60, [], []) : null);
        public ValueTask<ScheduledBusinessResult> ExecuteAsync(FrozenScheduledInvocation invocation, CancellationToken cancellation) => throw new InvalidOperationException();
        public ValueTask<ScheduledBusinessResult> ExecuteManualAsync(ManualInvocation invocation, CancellationToken cancellation)
        { Calls++; throw new InvalidOperationException("fixture_callback_forbidden"); }
    }
}
