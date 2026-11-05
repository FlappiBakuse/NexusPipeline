using System.Globalization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

public sealed class RecordSummaryTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "One Hundred Thousand Synthetic Saved Runs Keep Real Sql Totals Day And User Pages And Direct Run Reads")]
    public void PagesLargeHistory()
    {
        const int count = 100_000;
        const int usersCount = 202;
        const int batchSize = 250;
        var (database, store) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var users = Enumerable.Range(1, usersCount)
            .Select(number => Guid.Parse(number.ToString("D8", CultureInfo.InvariantCulture) + "-0000-0000-0000-000000000000"))
            .ToArray();
        var from = Utc(2026, 1, 1);
        for (var start = 0; start < count; start += batchSize)
            store.AppendCompleted(Enumerable.Range(start, Math.Min(batchSize, count - start))
                .Select(Saved).ToArray());

        var reader = new RecordSummaryReader(database);
        var query = new RecordSummaryQuery(from, from.AddDays(30), 0);
        var first = reader.Read(query);
        Assert.Equal(count, first.Totals.Runs);
        Assert.Equal(count, first.Totals.Succeeded);
        Assert.Equal(30, first.Days.Count);
        Assert.Equal(count, first.Days.Sum(day => day.Totals.Runs));
        Assert.Equal(3334, first.Days.Single(day => day.Date == "2026-01-01").Totals.Runs);
        Assert.Equal(3333, first.Days.Single(day => day.Date == "2026-01-30").Totals.Runs);
        Assert.Equal(200, first.Users.Count);
        Assert.Equal(users[199], first.NextUserId);
        var second = reader.Read(query with { AfterUserId = first.NextUserId });
        Assert.Equal(count, second.Totals.Runs);
        Assert.Equal(new Guid?[] { users[200], users[201] }, second.Users.Select(item => item.UserId).ToArray());
        Assert.Null(second.NextUserId);
        Assert.Equal(count, first.Users.Concat(second.Users).Sum(user => user.Totals.Runs));
        Assert.Equal(496, reader.Read(query with { UserId = users[0] }).Totals.Runs);
        foreach (var index in new[] { 0, count / 2, count - 1 })
            Assert.Equal(Saved(index), reader.Read(Saved(index).FinalAttempt.RunId));

        output.WriteLine($"SYNTHETIC_HISTORY_100K runtime={database.RuntimeRoot} database={database.DatabasePath} " +
            $"firstRunId={Saved(0).FinalAttempt.RunId:D} middleRunId={Saved(count / 2).FinalAttempt.RunId:D} " +
            $"lastRunId={Saved(count - 1).FinalAttempt.RunId:D} records={count} days=30 users={usersCount}");

        CompletedRunRecord Saved(int index) => RecordStoreFixture.Record(index, automation) with
        { UserId = users[index % usersCount], EndedAt = from.AddDays(index % 30).AddTicks(index) };
    }

    [Fact(DisplayName = "History Summaries Enforce A 366 Day Window And Aggregate Timezone Days And Duration Quality Without Retired Runs")]
    public void SummarizesBoundedHistory()
    {
        var (database, store) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var users = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var records = new[]
        {
            RecordStoreFixture.Record(700, automation) with
            { UserId = users[0], EndedAt = Utc(2026, 3, 1, 21, 30), DurationMilliseconds = 125 },
            RecordStoreFixture.Record(701, automation) with
            {
                UserId = users[1], EndedAt = Utc(2026, 3, 1, 22, 30), DurationMilliseconds = 500,
                DurationKind = RecordDurationKind.CheckpointLowerBound,
                EndTimeKind = EndTimeKind.ReconciledTerminalObservation,
                FinalAttempt = RecordStoreFixture.Record(701, automation).FinalAttempt with { Business = BusinessOutcome.Failed }
            },
            RecordStoreFixture.Record(702, automation) with
            {
                UserId = users[2], EndedAt = Utc(2026, 3, 2, 0, 30), DurationMilliseconds = 700,
                DurationKind = RecordDurationKind.CheckpointLowerBound,
                EndTimeKind = EndTimeKind.DeclinedRetryObservation,
                FinalAttempt = RecordStoreFixture.Record(702, automation).FinalAttempt with { Business = BusinessOutcome.Cancelled }
            },
            RecordStoreFixture.Record(703, automation) with
            {
                UserId = users[3], EndedAt = Utc(2026, 3, 2, 3, 0), DurationMilliseconds = null,
                DurationKind = RecordDurationKind.Unknown,
                EndTimeKind = EndTimeKind.ReconciledTerminalObservation,
                FinalAttempt = RecordStoreFixture.Record(703, automation).FinalAttempt with { Business = BusinessOutcome.Unverified }
            },
            RecordStoreFixture.Record(704, automation) with { UserId = users[4], EndedAt = Utc(2026, 3, 2, 11, 0) }
        };
        store.AppendCompleted(records);
        Retire(database, records[^1].FinalAttempt.RunId);

        var reader = new RecordSummaryReader(database);
        var from = Utc(2026, 3, 1);
        var until = Utc(2026, 3, 3);
        var summary = reader.Read(new RecordSummaryQuery(from, until, 120));

        Assert.Equal(new RecordTotals(4, 1, 1, 1, 1, 1, 125, 2, 1), summary.Totals);
        Assert.Collection(summary.Days,
            day => { Assert.Equal("2026-03-02", day.Date); Assert.Equal(3, day.Totals.Runs); },
            day => { Assert.Equal("2026-03-01", day.Date); Assert.Equal(1, day.Totals.Runs); });
        Assert.Equal(records[0], reader.Read(records[0].FinalAttempt.RunId));
        Assert.Null(reader.Read(records[^1].FinalAttempt.RunId));

        Assert.Equal(4, reader.Read(new RecordSummaryQuery(from, from.AddDays(366), 0)).Totals.Runs);
        Assert.Throws<ArgumentException>(() => reader.Read(new RecordSummaryQuery(from, from.AddDays(366).AddTicks(1), 0)));
        var cancelled = RecordStoreFixture.Record(705, automation) with { EndedAt = from.AddHours(1),
            FinalAttempt = RecordStoreFixture.Record(705, automation).FinalAttempt with { Lifecycle = LifecycleOutcome.Cancelled } };
        store.AppendCompleted([cancelled]);
        var filtered = reader.Read(new RecordSummaryQuery(from, until, 0, Lifecycle: LifecycleOutcome.Cancelled));
        Assert.Equal(1, filtered.Totals.Runs);
        Assert.Equal(1, filtered.Totals.Succeeded);
        Assert.Equal(1, Assert.Single(filtered.Days).Totals.Runs);
        Assert.Equal(1, Assert.Single(filtered.Users).Totals.Runs);
        Assert.Equal(0, reader.Read(new RecordSummaryQuery(from, until, 0, Business: BusinessOutcome.Failed,
            Lifecycle: LifecycleOutcome.Cancelled)).Totals.Runs);
        Assert.Throws<ArgumentException>(() => reader.Read(new RecordSummaryQuery(from, until, 0, Lifecycle: LifecycleOutcome.Running)));
    }

    [Fact(DisplayName = "Summary User Pages Continue By Stable Guid And A Run Detail Remains Directly Readable")]
    public void PagesUserHistory()
    {
        var (database, store) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var users = Enumerable.Range(1, 202)
            .Select(number => Guid.Parse(number.ToString("D8", CultureInfo.InvariantCulture) + "-0000-0000-0000-000000000000"))
            .ToArray();
        var records = users.Select((user, index) => RecordStoreFixture.Record(index, automation) with
        { UserId = user, EndedAt = Utc(2026, 4, 1).AddTicks(index) }).ToArray();
        store.AppendCompleted(records);

        var reader = new RecordSummaryReader(database);
        var query = new RecordSummaryQuery(Utc(2026, 4, 1), Utc(2026, 4, 2), 0);
        var first = reader.Read(query);
        Assert.Equal(202, first.Totals.Runs);
        Assert.Equal(200, first.Users.Count);
        Assert.Equal(users[199], first.NextUserId);

        var second = reader.Read(query with { AfterUserId = first.NextUserId });
        Assert.Equal(202, second.Totals.Runs);
        Assert.Equal(new Guid?[] { users[200], users[201] }, second.Users.Select(item => item.UserId).ToArray());
        Assert.Null(second.NextUserId);
        Assert.Equal(records[^1], reader.Read(records[^1].FinalAttempt.RunId));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static void Retire(StateDatabase database, Guid runId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE run_records SET history_retired_at_utc_ticks=$time WHERE run_id=$run;";
        command.Parameters.AddWithValue("$time", Utc(2026, 4, 1).UtcTicks);
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
