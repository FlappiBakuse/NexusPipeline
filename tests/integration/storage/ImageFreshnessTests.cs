using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ImageFreshnessTests
{
    [Fact(DisplayName = "Image Metadata Queries Recheck The Actual Android Sql Root Even After The Catalog Was Published")]
    public async Task RechecksAndroidRoots()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            forceClose: false, captureMode: "rgba");
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync(); var source = fixture.Source;
            new ImageCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(receipt);
            var reader = new ImageCatalogReader(source.Instance, source.Database, new());
            var query = new NexusPipeline.Core.Records.ImageCatalogQuery(receipt.Facts.RunId, receipt.Facts.AttemptId);
            var original = Assert.Single(reader.ReadPage(query).Items);
            var state = fixture.State;
            var command = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)[0];
            var root = TargetRootFacts.ReadCommand(source.Database, command)!;
            var where = " WHERE journal_id='" + receipt.JournalId.ToString("D") + "' AND target_id='" + command.Binding.TargetId.ToString("D") + "';";
            fixture.Base.Execute("UPDATE execution_target_job_roots SET image_sha256='" + new string('0', 64) + "'" + where);
            try { Assert.ThrowsAny<IOException>(() => reader.ReadPage(query)); }
            finally { fixture.Base.Execute("UPDATE execution_target_job_roots SET image_sha256='" + root.ImageSha256 + "'" + where); }
            Assert.Equal(original, Assert.Single(reader.ReadPage(query).Items)); fixture.VerifyOriginal();
        });
    }
}
