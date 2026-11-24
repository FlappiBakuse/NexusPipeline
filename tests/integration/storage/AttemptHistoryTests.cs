using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage.Automation;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AttemptHistoryTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "A Full Receipt Page Reads Its Actual SQLite Snapshot With One Select")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    public void ReadsOneReceiptPageQuery(int count)
    {
        var fixture = new ConfigurationBatchFixture();
        var runId = Guid.NewGuid();
        var expected = SeedReceipts(fixture.Database, runId, count);
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true, allowPooling: false,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var query = new AttemptHistoryQuery(runId, Limit: 100);
        var selects = new List<string>();
        SQLitePCL.strdelegate_trace record = (_, sql) =>
        {
            if (sql.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)) selects.Add(sql);
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, record, null);
        AttemptHistoryPage page;
        try { page = AttemptHistoryReader.ReadInside(connection, transaction, query); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace?)null, null); }

        Assert.Equal(expected, page.Items);
        Assert.Null(page.NextOrdinal);
        var iterations = count == 99 ? 300 : 2000;
        for (var i = 0; i < 50; i++) _ = AttemptHistoryReader.ReadInside(connection, transaction, query);
        using var process = Process.GetCurrentProcess();
        var cpuStarted = process.TotalProcessorTime;
        var allocatedStarted = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) _ = AttemptHistoryReader.ReadInside(connection, transaction, query);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStarted;
        var cpu = process.TotalProcessorTime - cpuStarted;
        output.WriteLine($"attempt-page rows={count} selects={selects.Count} repetitions={iterations} elapsed-ms={elapsed.TotalMilliseconds:F3} cpu-ms={cpu.TotalMilliseconds:F3} allocated-bytes={allocated}");
        foreach (var sql in selects.Distinct(StringComparer.Ordinal).Select(Shape).Distinct(StringComparer.Ordinal))
        {
            using var explain = connection.CreateCommand(); explain.Transaction = transaction;
            explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
            var parameters = new (string Name, object Value)[] { ("$run", runId.ToString("D")), ("$ordinal", 0),
                ("$limit", 101), ("$id", (expected.FirstOrDefault()?.Facts.AttemptId ?? Guid.Empty).ToString("D")) };
            foreach (var (name, value) in parameters)
                if (sql.Contains(name, StringComparison.Ordinal)) explain.Parameters.AddWithValue(name, value);
            using var planner = explain.ExecuteReader();
            while (planner.Read()) output.WriteLine(planner.GetString(3));
        }
        Assert.Single(selects);
    }

    [Fact(DisplayName = "The Lookahead Checks Keys And Leaves Its Unreturned Receipt For The Next Page")]
    public void PreservesLookaheadValidation()
    {
        var fixture = new ConfigurationBatchFixture();
        var runId = Guid.NewGuid();
        var expected = SeedReceipts(fixture.Database, runId, 3);
        var reader = new AttemptHistoryReader(fixture.Database);
        void Update(Guid run, string sql, object value)
        {
            using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath,
                domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
            using var command = DatabaseConnections.Command(connection, null, sql, value, run.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        Update(runId, "UPDATE records.attempt_receipts SET journal_id=$0 WHERE run_id=$1 AND ordinal=2;", "malformed-journal");
        var first = reader.ReadPage(new(runId, Limit: 1));
        Assert.Equal(expected[0], Assert.Single(first.Items)); Assert.Equal(1, first.NextOrdinal);
        Assert.Throws<FormatException>(() => reader.ReadPage(new(runId, AfterOrdinal: 1, Limit: 1)));
        Assert.Equal(expected[2], Assert.Single(reader.ReadPage(new(runId, AfterOrdinal: 2)).Items));
        Update(runId, "UPDATE records.attempt_receipts SET journal_id=$0 WHERE run_id=$1 AND ordinal=2;", expected[1].JournalId.ToString("D"));
        Update(runId, "UPDATE records.attempt_receipts SET reason_code=$0 WHERE run_id=$1 AND ordinal=2;", "committed_later");
        Assert.Equal("committed_later", Assert.Single(reader.ReadPage(new(runId, AfterOrdinal: 1, Limit: 1)).Items).Facts.ReasonCode);

        var corruptRun = Guid.NewGuid();
        _ = SeedReceipts(fixture.Database, corruptRun, 2, malformedLastKey: true);
        const string invalid = "attempt_history_metadata_unrecognized";
        Assert.Equal(invalid, Assert.Throws<IOException>(() => reader.ReadPage(new(corruptRun, Limit: 1))).Message);
        Update(corruptRun, "UPDATE records.attempt_receipts SET journal_id=$0 WHERE run_id=$1 AND ordinal=1;", "invalid-first-journal");
        Assert.Equal(invalid, Assert.Throws<IOException>(() => reader.ReadPage(new(corruptRun, Limit: 1))).Message);
    }

    private static string Shape(string sql)
    {
        // The legacy lookup repeats this statement with a different bound attempt ID for each page row.
        if (!sql.Contains("WHERE attempt_id=", StringComparison.Ordinal)) return sql;
        return sql[..(sql.IndexOf("WHERE attempt_id=", StringComparison.Ordinal) + "WHERE attempt_id=".Length)] + "$id;";
    }

    private static AttemptReceipt[] SeedReceipts(StateDatabase database, Guid runId, int count, bool malformedLastKey = false)
    {
        var receipts = Enumerable.Range(1, count).Select(ordinal => new AttemptReceipt(Guid.NewGuid(), Guid.NewGuid(),
            new('a', 64), new('b', 64), new(runId, Guid.NewGuid(), ordinal, EngineOutcome.Succeeded,
                BusinessOutcome.Succeeded, LifecycleOutcome.Completed, RecoveryOutcome.NotRequired, "fixture_succeeded"))).ToArray();
        using var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = connection.BeginTransaction();
        foreach (var receipt in receipts.Reverse())
        {
            var facts = receipt.Facts;
            var attemptId = malformedLastKey && facts.Ordinal == count ? "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF" : facts.AttemptId.ToString("D");
            using var command = DatabaseConnections.Command(connection, transaction,
                "INSERT INTO records.attempt_receipts VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11);",
                attemptId, runId.ToString("D"), facts.Ordinal, receipt.JournalId.ToString("D"),
                receipt.OperationId.ToString("D"), receipt.PlanDigest, receipt.SourceReceiptDigest, (int)facts.Engine,
                (int)facts.Business, (int)facts.Lifecycle, (int)facts.Recovery, facts.ReasonCode);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return receipts;
    }

    [Fact(DisplayName = "A Proven Unentered Attempt Reconciles History Without Inventing A Measured Terminal Boundary")]
    public void ReconcilesUnenteredAttempt()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var operation = Guid.NewGuid();
        var accepted = fixture.Service.Start(operation, "history-recovery", source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        fixture.Take(operation).Dispose();
        var recovery = new PreparedRecoverySession(source.Instance, accepted.JournalId, new(), source.Database, () =>
        {
            var observed = new JournalWriterStore(source.Database).ObserveForCurrentExecutor(accepted.JournalId);
            return new(source.Instance.InstanceId, accepted.JournalId, observed.IsComplete, observed.State);
        });
        Assert.Equal(NexusPipeline.Core.Configuration.JournalRecoveryResult.NotExecuted, new PreparedAttemptRecovery(recovery).Recover());
        var state = AttemptWriterState.Read(source.Instance, source.Database, new(), accepted.JournalId);
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(source.Database).Read(Guid.Parse(state.Attempt.AttemptId)));
        var finality = new ExecutionRunFinality(source.Instance, source.Database, new());
        Assert.False(finality.IsFinal(receipt));
        Assert.Throws<IOException>(() => finality.IsFinal(receipt with { SourceReceiptDigest = new string('0', 64) }));
        var records = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease, finality);
        records.ReconcileStopped(receipt); Assert.Null(records.ReconcileStoppedPage());
        var record = Assert.IsType<RecordDisplay>(new RecordDisplayReader(source.Instance, source.Database, new()).Read(receipt.Facts.RunId));
        Assert.Equal(receipt.Facts.AttemptId, Assert.Single(record.Attempts).AttemptId);
        Assert.Equal(LifecycleOutcome.Blocked, receipt.Facts.Lifecycle);
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Historical Display Uses Verified Frozen Names And Budget Timing After Catalog Names Change")]
    public async Task DisplaysFrozenRecord()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("success");
        var receipt = await fixture.RunAsync();
        var source = fixture.Source;
        new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new())).ReconcileStopped(receipt);
        var reader = new RecordDisplayReader(source.Instance, source.Database, new());
        var before = Assert.IsType<RecordDisplay>(reader.Read(receipt.Facts.RunId));
        Assert.Equal("Other", before.TargetName);
        Assert.Equal("manual", before.Mode);
        Assert.Equal(1, before.MaximumAttempts);
        Assert.NotNull(before.StartedAt);
        var timing = Assert.Single(before.Attempts);
        Assert.Equal(receipt.Facts.AttemptId, timing.AttemptId);
        Assert.NotNull(timing.EndedAt);
        Assert.NotNull(timing.DurationMilliseconds);
        Assert.True(timing.StartedAt <= timing.EndedAt);
        var reports = new ExecutionLogReader(source.Instance, source.Database, new());
        var invocation = Assert.Single(Assert.Single(reports.ReadReport(receipt.Facts.RunId, receipt.Facts.AttemptId).Steps).Invocations);
        Assert.NotNull(invocation.StartedAt);
        Assert.NotNull(invocation.EndedAt);
        Assert.True(invocation.StartedAt <= invocation.EndedAt);
        Assert.True(invocation.DurationMilliseconds >= 0);
        var catalog = source.Definitions.Read();
        var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
        var changes = new AutomationChanges(catalog.Revision, [], [script with { Name = "Renamed after run" }], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "record-display-fixture", changes),
            changes, DateTimeOffset.UtcNow);
        var after = Assert.IsType<RecordDisplay>(reader.Read(receipt.Facts.RunId));
        Assert.Equal(before.TargetName, after.TargetName);
        Assert.Equal(before.UserName, after.UserName);
        Assert.Equal(before.StartedAt, after.StartedAt);
        Assert.Equal(before.Attempts, after.Attempts);
        Assert.Equal(invocation, Assert.Single(Assert.Single(reports.ReadReport(receipt.Facts.RunId, receipt.Facts.AttemptId).Steps).Invocations));
        Assert.Null(reader.Read(Guid.NewGuid()));
    }

    [Fact(DisplayName = "A Real Successful Retry Keeps The Interrupted Attempt Visible In Stable Ordinal Pages")]
    public async Task KeepsInterruptedAttempts()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var first = await fixture.RunInterruptedAsync();
        var history = new AttemptHistoryReader(fixture.Source.Database);
        var before = history.ReadPage(new(first.Facts.RunId, Limit: 1));
        Assert.Equal(first, Assert.Single(before.Items)); Assert.Null(before.NextOrdinal);
        var retries = new ExecutionRetryService(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Service);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        _ = retries.Start(pending, DateTimeOffset.UtcNow);
        var second = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var page = history.ReadPage(new(first.Facts.RunId, Limit: 1));
        Assert.Equal(first, Assert.Single(page.Items)); Assert.Equal(1, page.NextOrdinal);
        var next = history.ReadPage(new(first.Facts.RunId, page.NextOrdinal!.Value, 1));
        Assert.Equal(second, Assert.Single(next.Items)); Assert.Null(next.NextOrdinal);
        Assert.Equal(LifecycleOutcome.Interrupted, page.Items[0].Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Succeeded, next.Items[0].Facts.Engine);
        Assert.Equal(first, Assert.Single(before.Items));
        Assert.Empty(history.ReadPage(new(Guid.NewGuid())).Items);
        Assert.Empty(history.ReadPage(new(first.Facts.RunId, AfterOrdinal: 99)).Items);
    }
}
