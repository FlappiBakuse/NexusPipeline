using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduledProviderRowsTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Provider Admission Bounds Warm Capacity Work And Shares The Limit With Ordinary Waiting Runs")]
    public void BoundsSharedWaitingCapacity()
    {
        using var fixture = new ScheduleNativeFixture();
        var path = fixture.Source.Database.DatabasePath;
        using var writer = DatabaseConnections.Open(path, allowPooling: false, domains: DatabaseDomain.Runtime);
        using (var setup = writer.BeginTransaction(deferred: false))
        {
            ScheduleCapacityHistory.Seed(writer, setup);
            ScheduledProviderRows.RequireCapacity(writer, setup, 1);
            setup.Commit();
        }

        var batches = 0;
        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            SQLitePCL.raw.sqlite3_progress_handler(writer.Handle!, 100, _ => { batches++; return 0; }, null);
            try { ScheduledProviderRows.RequireCapacity(writer, transaction, 1); }
            finally { SQLitePCL.raw.sqlite3_progress_handler(writer.Handle!, 0, null, null); }
            transaction.Commit();
        }

        using (var other = DatabaseConnections.Open(path, allowPooling: false, domains: DatabaseDomain.Runtime))
        using (var delete = DatabaseConnections.Command(other, null,
            "DELETE FROM main.operations WHERE operation_id BETWEEN $0 AND $1;",
            "923cebdf-3954-448f-b378-000000000001", "923cebdf-3954-448f-b378-000000004095"))
            Assert.Equal(4095, delete.ExecuteNonQuery());

        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            ScheduledProviderRows.RequireCapacity(writer, transaction, 1);
            Assert.Equal("schedule_waiting_capacity", Assert.Throws<IOException>(() =>
                ScheduledProviderRows.RequireCapacity(writer, transaction, 2)).Message);
            transaction.Commit();
        }
        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            using var admit = DatabaseConnections.Command(writer, transaction, """
                INSERT INTO main.operations(operation_id,subject_id,request_digest,operation_kind,status,created_at)
                VALUES('923cebdf-3954-448f-b378-000000000001','fixture:rebound',printf('%064d',1),
                       'execution.start','accepted','2026-01-01T00:00:00Z');
                """);
            Assert.Equal(1, admit.ExecuteNonQuery());
            ScheduledProviderRows.RequireCapacity(writer, transaction, 2);
            transaction.Rollback();
        }
        using (var transaction = writer.BeginTransaction(deferred: false))
            Assert.Equal("schedule_waiting_capacity", Assert.Throws<IOException>(() =>
                ScheduledProviderRows.RequireCapacity(writer, transaction, 2)).Message);

        output.WriteLine($"shared schedule capacity: 12000 historical occurrences, {batches} SQLite progress callbacks at 100 VM instructions");
        Assert.InRange(batches, 0, 20);
    }

    [Fact(DisplayName = "A Scheduled Provider Observation Reuses Its Bound Operation And Occurrence In One Snapshot")]
    public async Task ReadsBoundRowsOnce()
    {
        await using var fixture = new NotificationScheduledFixture { HoldCallback = true, SubmitNotification = false };
        await fixture.Activate();
        await fixture.Tick.TickAsync();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var pending = Assert.Single(fixture.States());
        Assert.Null(pending.Result);
        Assert.NotNull(pending.Begin);
        var source = fixture.Base.Source;
        var evidence = new ScheduledProviderEvidence(source.Instance, new());
        var id = pending.Source.Document.OperationId.ToString("D");

        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, domains: DatabaseDomain.Runtime))
        using (var transaction = connection.BeginTransaction())
        {
            using var digest = DatabaseConnections.Command(connection, transaction,
                "UPDATE main.operations SET request_digest=$0 WHERE operation_id=$1;", new string('0', 64), id);
            Assert.Equal(1, digest.ExecuteNonQuery());
            using var metadata = DatabaseConnections.Command(connection, transaction,
                "UPDATE main.scheduled_provider_occurrences SET provider_id=$0 WHERE occurrence_id=$1;", "different", id);
            Assert.Equal(1, metadata.ExecuteNonQuery());
            Assert.Throws<OperationConflictException>(() => evidence.Read(pending.Source.Reference, connection, transaction));
            using var reference = DatabaseConnections.Command(connection, transaction,
                "UPDATE main.scheduled_provider_occurrences SET size_bytes=size_bytes+1 WHERE occurrence_id=$0;", id);
            Assert.Equal(1, reference.ExecuteNonQuery());
            Assert.Equal("scheduled_provider_source_unbound",
                Assert.Throws<IOException>(() => evidence.Read(pending.Source.Reference, connection, transaction)).Message);
            transaction.Rollback();
        }

        var accepted = Inspect("accepted", pending);
        fixture.CallbackGate.TrySetResult();
        await fixture.PumpUntilResult();
        var completed = Assert.Single(fixture.States());
        Assert.Equal(pending.Source.Reference, completed.Source.Reference);
        Assert.Equal(pending.Begin, completed.Begin);
        Assert.Equal("returned", completed.Result!.Completion);
        Assert.Equal(1, fixture.Effects);
        Assert.Equal(1, fixture.PlannedCalls);
        var finished = Inspect("completed", completed);
        Assert.All(accepted.Concat(finished), statements =>
        {
            Assert.Single(statements, sql => sql.Contains(" FROM main.scheduled_provider_occurrences WHERE ", StringComparison.Ordinal));
            Assert.Single(statements, sql => sql.Contains(" FROM main.operations WHERE ", StringComparison.Ordinal));
        });

        IReadOnlyList<List<string>> Inspect(string state, ScheduledProviderState expected)
        {
            using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true,
                allowPooling: false, domains: DatabaseDomain.Runtime);
            using var transaction = DatabaseConnections.BeginRead(connection);
            ScheduledProviderState? observed = null;
            void Read() => observed = evidence.Read(expected.Source.Reference, connection, transaction);
            void RequireOpen() => ScheduledProviderRows.RequireOpen(connection, transaction, expected.Source, requireBegin: true);
            void RequireClosed() => Assert.Equal("scheduled_provider_dispatch_closed", Assert.Throws<IOException>(RequireOpen).Message);
            var reads = Observe("read", Read, 60);
            var guards = state == "accepted" ? Observe("open", RequireOpen, 200) : Observe("closed", RequireClosed, 60);
            var actual = Assert.IsType<ScheduledProviderState>(observed);
            Assert.Equal(expected.Source.Reference, actual.Source.Reference);
            Assert.Equal(expected.Source.Document.OperationId, actual.Source.Document.OperationId);
            Assert.Equal(expected.BeginReference, actual.BeginReference);
            Assert.Equal(expected.Begin, actual.Begin);
            Assert.Equal(expected.CloseReference, actual.CloseReference);
            Assert.Equal(expected.Close, actual.Close);
            Assert.Equal(expected.ResultReference, actual.ResultReference);
            Assert.Equal(expected.Result, actual.Result);
            return state == "accepted" ? [reads, guards] : [reads];

            List<string> Observe(string operation, Action call, int iterations)
            {
                var statements = new List<string>();
                SQLitePCL.strdelegate_trace record = (_, sql) =>
                {
                    if (sql.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)) statements.Add(sql);
                };
                SQLitePCL.raw.sqlite3_trace(connection.Handle!, record, null);
                try { call(); }
                finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace?)null, null); }
                for (var i = 0; i < 10; i++) call();
                using var process = Process.GetCurrentProcess();
                var cpuStarted = process.TotalProcessorTime;
                var allocatedStarted = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                for (var i = 0; i < iterations; i++) call();
                var elapsed = Stopwatch.GetElapsedTime(started);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStarted;
                var cpu = process.TotalProcessorTime - cpuStarted;
                output.WriteLine($"scheduled-provider state={state} operation={operation} selects={statements.Count} repetitions={iterations} elapsed-ms={elapsed.TotalMilliseconds:F3} cpu-ms={cpu.TotalMilliseconds:F3} allocated-bytes={allocated}");
                foreach (var sql in statements.Distinct(StringComparer.Ordinal))
                {
                    output.WriteLine(sql);
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                    if (sql.Contains("$0", StringComparison.Ordinal)) command.Parameters.AddWithValue("$0", id);
                    if (sql.Contains("$id", StringComparison.Ordinal))
                        command.Parameters.AddWithValue("$id", sql.Contains("schedule_minute_", StringComparison.Ordinal)
                            ? expected.Source.Document.MinuteOperationId.ToString("D") : id);
                    using var planner = command.ExecuteReader();
                    while (planner.Read()) output.WriteLine(planner.GetString(3));
                }
                return statements;
            }
        }
    }
}
