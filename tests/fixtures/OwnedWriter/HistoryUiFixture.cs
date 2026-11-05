using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class HistoryUiFixture
{
    internal static int Run(string[] args, bool reuse = false)
    {
        if (args is not [var root, var report]) return 2;
        var allowed = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Fixture root required"));
        root = Path.GetFullPath(root); report = Path.GetFullPath(report);
        if (!root.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !report.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(root) != reuse || File.Exists(report)) return 2;
        Directory.CreateDirectory(root);
        var contracts = new ContractReader();
        var roots = new InstanceRoot(contracts);
        var instance = reuse ? roots.OpenExisting(Path.Combine(root, "runtime")) : roots.OpenOrCreate(Path.Combine(root, "runtime"));
        using var lease = InstanceLease.Acquire(instance, contracts);
        var database = reuse ? StateDatabase.OpenExisting(instance, Path.Combine(root, "diagnostics")) : StateDatabase.CreateFresh(instance);
        var records = new RunRecordStore(database);
        var elapsed = Stopwatch.StartNew();
        const int count = HistoryReadSeed.Count;
        if (!reuse) HistoryReadSeed.Populate(records);
        var verified = Verify(records, count);
        var summary = new RecordSummaryReader(database).Read(new RecordSummaryQuery(From, From.AddDays(1), 0));
        if (summary.Totals.Runs != count || summary.Users.Count != 200 || summary.NextUserId is null)
            throw new IOException("History UI fixture totals differ");
        using var output = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(output, new
        {
            status = "PASS", scope = "Synthetic history read-model storage verification; not a complete recoverable Host runtime",
            storageOnly = true, fullHostStartupQualified = false, completeExecutionJournals = 0,
            instanceId = instance.InstanceId, instance.RuntimeRoot, records = count, users = 10001,
            firstUserId = Id("20000000", 1), firstUserRuns = 90000, firstRunId = Id("10000000", 1),
            lastRunId = Id("10000000", count), fromInclusive = From, untilExclusive = From.AddDays(1),
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            reusedExistingData = reuse, verifiedRecords = verified,
            databaseBytes = new FileInfo(database.DatabasePath).Length
        }, JsonSerializerOptions.Web);
        return 0;
    }

    private static int Verify(RunRecordStore records, int expected)
    {
        RecordCursor? cursor = null;
        var count = 0;
        do
        {
            var page = records.ReadPage(new(200, After: cursor));
            foreach (var record in page.Items)
            {
                if (count >= expected || record != Record(count))
                    throw new IOException("History UI fixture content differs");
                count++;
            }
            cursor = page.Next;
        } while (cursor is not null);
        if (count != expected) throw new IOException("History UI fixture count differs");
        return count;
    }

    private static DateTimeOffset From => HistoryReadSeed.From;
    private static Guid Id(string prefix, int value) => HistoryReadSeed.Id(prefix, value);
    private static CompletedRunRecord Record(int index) => HistoryReadSeed.Record(index);
}
