using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class HistoryDatasetFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var root, var report]) return 2;
        var allowed = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Fixture root required"));
        root = Path.GetFullPath(root);
        report = Path.GetFullPath(report);
        if (!root.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !report.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root)) return 2;
        Directory.CreateDirectory(root);
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        using var lease = InstanceLease.Acquire(instance, contracts);
        var database = StateDatabase.CreateFresh(instance);
        var records = new RunRecordStore(database);
        var stages = new List<object>();
        var previous = 0;
        foreach (var size in new[] { 10000, 100000 })
        {
            var elapsed = Stopwatch.StartNew();
            for (var start = previous; start < size; start += 256)
                records.AppendCompleted(Enumerable.Range(start, Math.Min(256, size - start)).Select(Record).ToArray());
            stages.Add(new { rows = size, appended = size - previous, appendMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                queries = MeasureQueries(records, size), databaseBytes = new FileInfo(database.DatabasePath).Length });
            previous = size;
            Console.WriteLine($"history rows verified: {size}");
        }
        var blobs = new ImmutableBlobStore(instance, contracts);
        var assets = CreateImages(root, instance, database, blobs);
        var backup = VerifyBackup(root, instance, database, assets, contracts);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            status = "PASS", scope = "P0 deterministic history/image/database backup experiment; no complete product backup or secrets portability approval",
            seed = "nexus-history-fixture-1800", sourceMode = "actual built fixture", processId = Environment.ProcessId,
            instanceId = instance.InstanceId, databaseSettings = database.ReadSettings(), stages, imageCount = assets.Count,
            imageBytesEach = 262144, imageDimensions = new[] { 64, 64 }, imagePadding = "Valid private ancillary npAD chunk; synthetic images, not real screenshot complexity",
            assets, backup
        }, JsonSerializerOptions.Web));
        return 0;
    }

    private static object MeasureQueries(RunRecordStore records, int expectedCount)
    {
        var plans = new List<object>();
        foreach (var query in new[] { new RecordQuery(), new(AutomationId: Id("automation", 2)), new(UserId: Id("user", 3)), new(Business: BusinessOutcome.Failed) })
        {
            var plan = records.ExplainPage(query);
            var timings = new List<double>();
            for (var repetition = 0; repetition < 25; repetition++)
            {
                var elapsed = Stopwatch.StartNew();
                var page = records.ReadPage(query);
                timings.Add(elapsed.Elapsed.TotalMilliseconds);
                if (page.Items.Count != 50) throw new IOException("Fixture first-page size differed");
            }
            var sorted = timings.Order().ToArray();
            plans.Add(new { query, plan, samplesMilliseconds = timings, p50Milliseconds = sorted[12], p95Milliseconds = sorted[23], maxMilliseconds = sorted[^1] });
        }
        RecordCursor? cursor = null;
        CompletedRunRecord? previous = null;
        var count = 0;
        do
        {
            var page = records.ReadPage(new(200, After: cursor));
            foreach (var record in page.Items)
            {
                if (previous is not null && (record.EndedAt < previous.EndedAt || record.EndedAt == previous.EndedAt &&
                    string.CompareOrdinal(record.FinalAttempt.RunId.ToString("D"), previous.FinalAttempt.RunId.ToString("D")) <= 0))
                    throw new IOException("Fixture keyset history repeated or reordered records");
                previous = record;
                count++;
            }
            cursor = page.Next;
        } while (cursor is not null);
        if (count != expectedCount) throw new IOException("Fixture keyset history omitted records");
        return new { samplesPerFilter = 25, boundedPageSize = 200, verifiedTotalRows = count, plans };
    }

    internal static int QueryExisting(string[] args)
    {
        if (args is not [var root, var report]) return 2;
        var allowed = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Fixture root required"));
        if (!Path.GetFullPath(root).StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(report).StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(report)) return 2;
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenExisting(Path.Combine(root, "runtime"));
        using var lease = InstanceLease.Acquire(instance, contracts);
        var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(report)!, "query-diagnostics"));
        var queries = MeasureQueries(new(database), 100000);
        File.WriteAllText(report, JsonSerializer.Serialize(new { status = "PASS", scope = "Independent per-filter query timings on retained P0 dataset", queries }, JsonSerializerOptions.Web));
        return 0;
    }

    private static List<RecordAsset> CreateImages(string root, RuntimeInstance instance, StateDatabase database, ImmutableBlobStore blobs)
    {
        var assets = new List<RecordAsset>();
        var store = new RecordAssetStore(database, blobs);
        var path = Path.Combine(root, "fixture-image-input.png");
        for (var index = 0; index < 1000; index++)
        {
            var bytes = PngDatasetImage.Create(index);
            File.WriteAllBytes(path, bytes);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var frozen = new FileTreeEntry("", FileObjectKind.File, bytes.LongLength, digest);
            var reference = blobs.PersistContent(path, frozen, CancellationToken.None);
            var asset = new RecordAsset(Record(index).FinalAttempt.RunId, reference.Sha256, reference.SizeBytes, RecordAssetKind.Screenshot);
            store.AttachVerified(asset);
            if (store.ReadForRun(asset.RunId).Items.Single() != asset) throw new IOException("Fixture asset reference did not round trip");
            assets.Add(asset);
        }
        File.Delete(path);
        if (Directory.GetFiles(Path.Combine(instance.RuntimeRoot, "blobs/content")).Length != 1000) throw new IOException("Fixture content set differs");
        return assets;
    }

    private static object VerifyBackup(string root, RuntimeInstance source, StateDatabase database, IReadOnlyList<RecordAsset> assets, ContractReader contracts)
    {
        var destination = Path.Combine(root, "restored-fixture");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(destination, "state"));
        var backup = database.BackupTo(Path.Combine(destination, "state/nexus.db"));
        var identity = JsonNode.Parse(File.ReadAllBytes(Path.Combine(source.RuntimeRoot, "instance.json")))!.AsObject();
        identity["runtimeRoot"] = destination;
        var identityBytes = JsonSerializer.SerializeToUtf8Bytes(identity);
        using (contracts.Read(identityBytes, "instance.schema.json")) { }
        File.WriteAllBytes(Path.Combine(destination, "instance.json"), identityBytes);
        File.WriteAllBytes(Path.Combine(destination, "state-initialization.guard"), []);
        var restored = new InstanceRoot(contracts).OpenExisting(destination);
        var verifiedDatabase = StateDatabase.OpenExisting(restored, Path.Combine(root, "restore-diagnostics"));
        if (new RunRecordStore(verifiedDatabase).ReadPage(new()).Items.Count != 50) throw new IOException("Restored history is unreadable");
        var targetBlobs = new ImmutableBlobStore(restored, contracts);
        var sourceBlobs = new ImmutableBlobStore(source, contracts);
        var restoredAssets = new RecordAssetStore(verifiedDatabase, targetBlobs);
        foreach (var asset in assets)
        {
            var reference = new BlobReference($"blobs/content/{asset.Sha256}.bin", asset.SizeBytes, asset.Sha256);
            using var input = sourceBlobs.OpenVerified(reference);
            var target = Path.Combine(destination, reference.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output, 65536); output.Flush(flushToDisk: true); }
            targetBlobs.Verify(reference);
            if (restoredAssets.ReadForRun(asset.RunId).Items.Single() != asset) throw new IOException("Restored asset reference differs");
        }
        return new { databaseBackup = backup, restoredRoot = destination, restoredReferences = assets.Count, instanceIdentityRetained = restored.InstanceId == source.InstanceId,
            method = "SQLite backup API plus verified immutable content; fixture-only explicit instance path rebinding; restored instance never launched" };
    }

    private static CompletedRunRecord Record(int number) => new(Id("automation", number % 20), Id("user", number % 50),
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(number / 8), number % 600000,
        new(Id("run", number), Id("attempt", number), 1, number % 10 == 0 ? EngineOutcome.Failed : EngineOutcome.Succeeded,
            number % 10 == 0 ? BusinessOutcome.Failed : BusinessOutcome.Succeeded, LifecycleOutcome.Completed, RecoveryOutcome.NotRequired, "fixture_completed"));

    private static Guid Id(string kind, int number) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"nexus-history-fixture-1800:{kind}:{number}"))[..16]);
}
