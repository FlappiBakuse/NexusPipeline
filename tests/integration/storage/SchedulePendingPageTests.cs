using System.Runtime.Versioning;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SchedulePendingPageTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "A Waiting Page Reads Its First Real Pending Occurrence Without Scanning Finished History")]
    public void BoundsFirstWaitingPage()
    {
        var clock = new ScheduleNativeFixture.FrozenScheduleClock(PlanBuilderFixture.CreatedAt);
        using var fixture = new ScheduleNativeFixture(clock);
        var source = fixture.Source;
        source.Lease.BindDatabase(source.Database);
        var path = source.Database.DatabasePath;
        using (var writer = DatabaseConnections.Open(path))
        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            ScheduleCapacityHistory.Seed(writer, transaction);
            transaction.Commit();
        }
        var pending = Assert.Single(fixture.Store.CommitCurrentMinute());

        var batches = Measure(path, DatabaseDomain.Runtime, "ORDER BY c.trigger_utc_minute,c.occurrence_id LIMIT",
            () => Assert.Equal(pending, Assert.Single(fixture.Store.ReadWaiting(1))));
        Assert.Equal(pending, Assert.Single(fixture.Store.ReadWaiting(1)));
        var blocked = fixture.Store.BlockWaiting(pending, "schedule_source_cancelled");
        Assert.Equal(ScheduleOccurrencePhase.Blocked, blocked.Phase);
        Assert.Empty(fixture.Store.ReadWaiting(1));
        output.WriteLine($"first waiting page: 12000 historical occurrences, {batches} SQLite progress callbacks at 100 VM instructions");
        Assert.InRange(batches, 0, 100);
    }

    [Fact(DisplayName = "A Due Minute Registers Waiting Activity Without Rechecking Finished Occurrences")]
    public void BoundsWaitingRegistration()
    {
        var clock = new ScheduleNativeFixture.FrozenScheduleClock(PlanBuilderFixture.CreatedAt);
        using var fixture = new ScheduleNativeFixture(clock);
        var source = fixture.Source;
        source.Lease.BindDatabase(source.Database);
        var path = source.Database.DatabasePath;
        using (var writer = DatabaseConnections.Open(path))
        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            ScheduleCapacityHistory.Seed(writer, transaction);
            ScheduledProviderRows.RequireCapacity(writer, transaction, 0);
            transaction.Commit();
        }

        ScheduledOccurrence? pending = null;
        var batches = Measure(path, DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.System,
            "c.occurrence_id,0,c.first_operation_id", () => pending = Assert.Single(fixture.Store.CommitCurrentMinute()));
        Assert.Equal(pending, Assert.Single(fixture.Store.ReadWaiting(1)));
        Assert.Equal(fixture.ScheduleId, pending!.Frozen.ScheduleId);
        output.WriteLine($"waiting registration: 12000 historical occurrences, {batches} SQLite progress callbacks at 100 VM instructions");
        Assert.InRange(batches, 0, 100);
    }

    private static int Measure(string path, DatabaseDomain readDomains, string statement, Action read)
    {
        var batches = 0;
        var statementReached = false;
        SQLitePCL.strdelegate_trace trace = (_, sql) => statementReached |= sql.Contains(statement, StringComparison.Ordinal);
        var readerHandle = Instrument(readOnly: true, readDomains);
        var writerHandle = Instrument(readOnly: false, DatabaseDomain.All);
        try { read(); }
        finally
        {
            foreach (var handle in new[] { readerHandle, writerHandle })
            {
                SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null);
                SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null);
            }
        }
        Assert.True(statementReached);
        return batches;

        SQLitePCL.sqlite3 Instrument(bool readOnly, DatabaseDomain domains)
        {
            using var connection = DatabaseConnections.Open(path, readOnly: readOnly, domains: domains);
            var handle = connection.Handle!;
            SQLitePCL.raw.sqlite3_progress_handler(handle, 100, _ => { batches++; return 0; }, null);
            SQLitePCL.raw.sqlite3_trace(handle, trace, null);
            return handle;
        }
    }
}
