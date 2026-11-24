using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class HistoryReadSeed
{
    internal const int Count = 100000;
    internal static DateTimeOffset From => new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
    internal static Guid Id(string prefix, int ordinal) => Guid.Parse($"{prefix}-0000-4000-8000-{ordinal:x12}");

    internal sealed record DatabaseFileSize(string Domain, long SizeBytes);

    internal static (long DatabaseBytes, long SidecarBytes, DatabaseFileSize[] DatabaseFiles) MeasureDatabase(StateDatabase database)
    {
        var primary = DatabaseLayout.Domains.Select(domain =>
            (Domain: DatabaseLayout.Name(domain), Path: DatabaseLayout.PathFor(database.DatabasePath, domain))).ToArray();
        var files = primary.Select(file => new DatabaseFileSize(file.Domain, new FileInfo(file.Path).Length)).ToArray();
        var suffixes = new[] { "-journal", "-wal", "-shm" };
        var sidecars = primary.SelectMany(file => suffixes.Select(suffix => file.Path + suffix))
            .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(database.DatabasePath)!, "runtime.db-mj*")
                .Where(path => DatabaseJournals.Known(Path.GetFileName(path))));
        return (files.Sum(file => file.SizeBytes), sidecars.Where(File.Exists).Sum(path => new FileInfo(path).Length), files);
    }

    internal static void Populate(RunRecordStore records)
    {
        for (var start = 0; start < Count; start += 256)
            records.AppendCompleted(Enumerable.Range(start, Math.Min(256, Count - start)).Select(Record).ToArray());
    }

    internal static CompletedRunRecord Record(int index) => new(Id("40000000", 1),
        Id("20000000", index < 90000 ? 1 : index - 89998), From.AddTicks(index), 120000,
        new(Id("10000000", index + 1), Id("30000000", index + 1), 1, EngineOutcome.Succeeded,
            BusinessOutcome.Succeeded, LifecycleOutcome.Completed, RecoveryOutcome.NotRequired, "fixture_completed"));
}
