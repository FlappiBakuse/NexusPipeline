using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CatalogCapacityTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "One Hundred Thousand Controlled Records And Five Thousand Generated Pngs Keep Indexed Date And Asset Pages Bounded Across Reopen")]
    public async Task BoundsIndexedPages()
    {
        var parent = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ??
            throw new InvalidOperationException("NEXUS_TEST_ROOT required");
        var root = Path.Combine(parent, "record-capacity-" + Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        var phases = new List<object>(); var queries = new List<object>();
        await using var probe = new CatalogCapacityProbe();
        var totalStart = probe.Observe();
        (long Bytes, RecordAsset First, RecordAsset[] Samples) images;
        using (var lease = InstanceLease.Acquire(instance, contracts))
        {
            var database = StateDatabase.CreateFresh(instance);
            var records = new RunRecordStore(database);
            var blobs = new ImmutableBlobStore(instance, contracts);
            var assets = new RecordAssetStore(database, blobs);
            var start = probe.Observe();
            CatalogCapacityData.WriteRecords(records);
            Phase("write100000RecordsInBatchesOf256", start);
            start = probe.Observe();
            images = CatalogCapacityData.WritePngs(blobs, assets);
            Phase("generatePersistAndAttach5000Unique160x90RgbPngs", start);
            Assert.InRange(images.Bytes, 200_000_000L, (long)CatalogCapacityData.PngCount * 65536);
            RequireCounts(database, images.Bytes);
            start = probe.Observe();
            foreach (var item in CatalogCapacityData.Queries())
            {
                var plan = records.ExplainPage(item.Query);
                RequireRecordIndex(plan, item.Name != "all");
                var count = CatalogCapacityData.Scan(records, item.Query);
                using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
                using var sql = RecordPageQuery.Create(connection, item.Query);
                queries.Add(new { item.Name, count, item.Query.Limit, sql = sql.CommandText,
                    parameters = sql.Parameters.Cast<Microsoft.Data.Sqlite.SqliteParameter>()
                        .Select(value => new { value.ParameterName, value.Value }).ToArray(), plan });
            }
            Phase("recordPagesAllFiltersDatesAndStableCursors", start);
            start = probe.Observe();
            ReadAssetPages(assets);
            var assetPlan = assets.ExplainPage(images.First.RunId, limit: 17);
            Assert.Contains(assetPlan, line => line.Contains("SEARCH a", StringComparison.Ordinal));
            Assert.DoesNotContain(assetPlan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
            queries.Add(new { name = "assets", plan = assetPlan });
            RequireMetadataOnly(root, records, assets, images.First);
            OpenSamples(assets, images.Samples);
            Phase("assetMetadataPagesExclusivePngLockAndThreeOriginalSamples", start);
        }
        var reopenStart = probe.Observe();
        using (var reopenedLease = InstanceLease.Acquire(instance, contracts))
        {
            var reopened = StateDatabase.OpenExisting(instance, Path.Combine(root, "startup-inspection"),
                new JournalStartupAdmission(instance, contracts));
            Phase("stateDatabaseOpenExistingAndActualJournalStartupAdmission", reopenStart);
            var start = probe.Observe();
            RequireCounts(reopened, images.Bytes);
            var records = new RunRecordStore(reopened);
            Assert.Equal(CatalogCapacityData.RecordCount,
                CatalogCapacityData.Scan(records, new RecordQuery(200)));
            var assets = new RecordAssetStore(reopened, new(instance, contracts));
            ReadAssetPages(assets);
            OpenSamples(assets, images.Samples);
            Phase("postReopenRecordScanAssetPagesAndOriginalSamples", start);
        }
        var totalEnd = probe.Observe();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            scope = "Controlled SQLite storage dataset, not actual execution or Native capture",
            controlledRecords = CatalogCapacityData.RecordCount,
            automationCount = 20, userCount = 50, generatedPngs = CatalogCapacityData.PngCount,
            imageOwners = CatalogCapacityData.Owners, imagesPerOwner = CatalogCapacityData.ImagesPerOwner,
            pngWidth = 160, pngHeight = 90, uniquePngBytes = images.Bytes,
            durationMilliseconds = totalEnd.Milliseconds - totalStart.Milliseconds,
            cpuMilliseconds = totalEnd.CpuMilliseconds - totalStart.CpuMilliseconds,
            processWideAllocatedBytes = totalEnd.AllocatedBytes - totalStart.AllocatedBytes,
            phases, queries, samples = probe.SampledRanges(),
            bounds = "Record and image metadata pages <=200; at most one PNG byte stream and one metadata page retained per operation",
            exclusions = "No actual attempt receipt, Native capture, complete Host startup, desktop or leak claim. " +
                "Startup admission has 100000 generic storage rows and 5000 asset relations, with no execution journal family. " +
                "Cumulative testhost allocation includes fixture generation, query oracle and sampler. Sampled memory is not a continuous peak. " +
                "Startup inspection copies database diagnostics, never the 5000 PNG files. No performance pass threshold is inferred."
        }));
        void Phase(string name, RecordCapacityObservation before)
        {
            var after = probe.Observe();
            phases.Add(new { name, elapsedMilliseconds = after.Milliseconds - before.Milliseconds,
                cpuMilliseconds = after.CpuMilliseconds - before.CpuMilliseconds,
                processWideAllocatedBytes = after.AllocatedBytes - before.AllocatedBytes });
        }
    }

    private static void RequireCounts(StateDatabase database, long pngBytes)
    {
        Assert.Equal((long)CatalogCapacityData.RecordCount, Scalar(database, "SELECT count(*) FROM run_records;"));
        Assert.Equal(20L, Scalar(database, "SELECT count(DISTINCT automation_id) FROM run_records;"));
        Assert.Equal(50L, Scalar(database, "SELECT count(DISTINCT user_id) FROM run_records;"));
        Assert.Equal((long)CatalogCapacityData.PngCount, Scalar(database, "SELECT count(*) FROM record_assets;"));
        Assert.Equal((long)CatalogCapacityData.PngCount, Scalar(database, "SELECT count(DISTINCT digest) FROM record_assets;"));
        Assert.Equal((long)CatalogCapacityData.Owners, Scalar(database, "SELECT count(DISTINCT run_id) FROM record_assets;"));
        Assert.Equal((long)CatalogCapacityData.PngCount, Scalar(database, "SELECT count(*) FROM blobs;"));
        Assert.Equal(pngBytes, Scalar(database, "SELECT sum(size_bytes) FROM blobs;"));
        Assert.Equal(0L, Scalar(database, "SELECT count(*) FROM attempt_receipts;"));
        Assert.Equal(0L, Scalar(database, "SELECT count(*) FROM execution_start_sources;"));
        Assert.Equal(0L, Scalar(database, "SELECT count(*) FROM execution_image_catalog;"));
    }

    private static long Scalar(StateDatabase database, string sql)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void RequireRecordIndex(IReadOnlyList<string> plan, bool boundedSearch)
    {
        Assert.Contains(plan, line => line.Contains("USING INDEX run_records_", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        if (boundedSearch) Assert.Contains(plan, line => line.Contains("SEARCH run_records", StringComparison.Ordinal));
    }

    private static void ReadAssetPages(RecordAssetStore assets)
    {
        for (var owner = 0; owner < CatalogCapacityData.Owners; owner++)
        {
            var run = CatalogCapacityData.RunId(owner);
            var count = 0; RecordAsset? previous = null; RecordAssetCursor? cursor = null;
            do
            {
                var page = assets.ReadForRun(run, cursor, 17); Assert.InRange(page.Items.Count, 0, 17);
                foreach (var asset in page.Items)
                {
                    Assert.Equal(run, asset.RunId); Assert.Equal(RecordAssetKind.Screenshot, asset.Kind);
                    if (previous is not null) Assert.True(StringComparer.Ordinal.Compare(previous.Sha256, asset.Sha256) < 0);
                    previous = asset; count++;
                }
                if (cursor is null && page.Next is not null)
                {
                    var plan = assets.ExplainPage(run, page.Next, 17);
                    Assert.Contains(plan, line => line.Contains("SEARCH a", StringComparison.Ordinal));
                    Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
                }
                cursor = page.Next;
            } while (cursor is not null);
            Assert.Equal(CatalogCapacityData.ImagesPerOwner, count);
        }
    }

    private static void RequireMetadataOnly(string root, RunRecordStore records,
        RecordAssetStore assets, RecordAsset sample)
    {
        using var held = new FileStream(Path.Combine(root, "blobs", "content", sample.Sha256 + ".bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Contains(sample, assets.ReadForRun(sample.RunId, limit: 200).Items);
        Assert.NotEmpty(records.ReadPage(new(200)).Items);
        Assert.Throws<IOException>(() => { using var original = assets.OpenVerified(sample); });
    }

    private static void OpenSamples(RecordAssetStore assets, IReadOnlyList<RecordAsset> samples)
    {
        Assert.Equal(3, samples.Count);
        foreach (var sample in samples)
        {
            using var original = assets.OpenVerified(sample);
            Assert.Equal(sample.SizeBytes, original.Length);
            Assert.Equal(sample.Sha256, Convert.ToHexStringLower(SHA256.HashData(original)));
            original.Position = 0; RgbCapturePng.Verify(original, 160, 90);
        }
    }
}
