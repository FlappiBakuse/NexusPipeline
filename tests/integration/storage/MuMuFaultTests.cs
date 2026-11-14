using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MuMuFaultTests
{
    [Theory(DisplayName = "Only Matching Complete Before And After Observations Can Publish An Image")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiresMatchingObservations(bool changedAfterImage)
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            forceClose: false, captureMode: changedAfterImage ? "mappingAfter" : "rgb");
        var observer = new CaptureObserver(point =>
        {
            if (!changedAfterImage && point == TargetPersistencePoint.CaptureSourcePublished)
                fixture.ChangeReportedPort(16385);
        });
        await RunAsync(fixture, observer, receipt =>
        {
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            if (changedAfterImage)
            {
                using var actualManager = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(
                    Path.Combine(fixture.Directory, "fixture-device.json")));
                Assert.Equal(16385, actualManager.RootElement.GetProperty("reportedPort").GetInt32());
            }
            var saved = AndroidCaptureTests.ReadCapture(fixture);
            Assert.Equal("unavailable", saved.Result!.Outcome);
            Assert.Equal("execution_capture_mapping_unverified", saved.Result.ReasonCode);
            Assert.Null(saved.Result.Png); Assert.Null(saved.Result.Image);
            Assert.Equal(changedAfterImage ? 3 : 1, saved.Commands.Length);
            Assert.Equal(changedAfterImage, File.Exists(Path.Combine(fixture.ScreenshotDirectory!, "fixture-screenshot.png")));
            if (!changedAfterImage) Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.ScreenshotDirectory!, "observed")));
            Assert.All(saved.Commands, command => Assert.NotNull(command.Observation));
        });
    }

    [Theory(DisplayName = "Accepted Source Cancel Or An Initial Broker Root Fault Cannot Start Any Paired Or Mapping Child")]
    [InlineData("sourceCancel")]
    [InlineData("rootFailure")]
    public async Task RejectsSourceCancellation(string fault)
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true, forceClose: false);
        var reached = false;
        var observer = new CaptureObserver(point =>
        {
            if (fault == "sourceCancel" && point == TargetPersistencePoint.CaptureSourcePublished)
            { reached = true; fixture.Cancel(); }
            if (fault == "rootFailure" && point == TargetPersistencePoint.CaptureRootPublished)
            { reached = true; throw new IOException("fixture_mumu_broker_before_resume"); }
        });
        await RunAsync(fixture, observer, receipt =>
        {
            Assert.True(reached);
            Assert.Equal(fault == "sourceCancel" ? LifecycleOutcome.Cancelled : LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var saved = AndroidCaptureTests.ReadCapture(fixture);
            Assert.Equal("unavailable", saved.Result!.Outcome); Assert.Empty(saved.Commands);
            Assert.Equal(fault == "sourceCancel", saved.Root is null);
            Assert.Null(saved.Result.Png); Assert.Null(saved.Result.Image);
            Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.ScreenshotDirectory!, "observed")));
        });
    }

    [Theory(DisplayName = "Saved Mapping And Source Facts Remain Bound While Sql Publication Is Atomic And Metadata Does Not Read Raw Mapping Content")]
    [InlineData("mappingRaw")]
    [InlineData("sourceIndex")]
    [InlineData("sqlRollback")]
    public async Task BindsAtomicMappings(string fault)
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            forceClose: false, captureMode: "rgba");
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync(); var source = fixture.Source;
            var saved = AndroidCaptureTests.ReadCapture(fixture);
            Assert.Equal("captured", saved.Result!.Outcome);
            var originalJournal = new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export();
            var projection = new ImageCatalogProjection(source.Instance, source.Database, new(), source.Lease);
            if (fault == "sqlRollback")
            {
                fixture.Base.Execute("CREATE TRIGGER records.fixture_mumu_image BEFORE INSERT ON execution_image_entries " +
                    "BEGIN SELECT RAISE(ABORT,'fixture_mumu_image'); END;");
                try
                {
                    Assert.Throws<SqliteException>(() => projection.OnCommitted(receipt));
                    Assert.Equal(0, fixture.Base.Count("execution_image_catalog")); Assert.Equal(0, fixture.Base.Count("execution_image_entries"));
                }
                finally { fixture.Base.Execute("DROP TRIGGER records.fixture_mumu_image;"); }
            }
            projection.OnCommitted(receipt);
            var reader = new ImageCatalogReader(source.Instance, source.Database, new());
            var image = Assert.Single(reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            if (fault != "sqlRollback")
            {
                var reference = fault == "mappingRaw" ? saved.Commands[0].Observation!.StandardOutput.Parts[0] : saved.State.Source;
                var path = Path.Combine(source.Instance.RuntimeRoot, reference.RelativePath); var original = File.ReadAllBytes(path);
                if (fault == "mappingRaw")
                {
                    var changed = original.ToArray(); changed[0] ^= 1; File.WriteAllBytes(path, changed);
                }
                else
                {
                    var node = JsonNode.Parse(original)!.AsObject(); node["android"]!["muMu"]!["index"] = 1;
                    File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node));
                }
                try
                {
                    if (fault == "mappingRaw") Assert.Equal(image, Assert.Single(reader.ReadPage(new(image.Key.RunId, image.Key.AttemptId)).Items));
                    else Assert.ThrowsAny<IOException>(() => reader.ReadPage(new(image.Key.RunId, image.Key.AttemptId)));
                    Assert.ThrowsAny<IOException>(() => reader.OpenOriginal(image.Key, image.ResultSha256));
                }
                finally { File.WriteAllBytes(path, original); }
            }
            using var verified = reader.OpenOriginal(image.Key, image.ResultSha256);
            Assert.Equal(image.SizeBytes, verified.Length);
            Assert.Equal(originalJournal, new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export());
            Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
        });
    }

    [Fact(DisplayName = "An Actual Captured Append And Main Closure Cannot Replace The Still Missing Android Lifecycle Collection")]
    public async Task RequiresLifecycleCollection()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            configuration: true, forceClose: false, captureMode: "rgba");
        AttemptWriterState? parentStopped = null; var checkedGap = false;
        var observer = new CaptureObserver(point =>
        {
            if (point == TargetPersistencePoint.CaptureParentStopped) parentStopped = fixture.State;
            if (point != TargetPersistencePoint.CaptureCollectedAppended) return;
            var state = Assert.IsType<AttemptWriterState>(parentStopped);
            var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId);
            var saved = Assert.Single(new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                .ReadMetadata(state.Journal, state.Attempt, state.Total, history));
            Assert.Equal("captured", saved.Result!.Outcome);
            Assert.NotNull(saved.Parent); Assert.Equal(0u, saved.Result.HelperStopped!.ActiveProcesses);
            Assert.Empty(AndroidCollectedCodec.Read(history[^1].Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            var pending = Assert.Throws<IOException>(() => AndroidCollectedCodec.RequireResolved(
                history[^1].Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal("execution_target_collection_pending", pending.Message); checkedGap = true;
        });
        await RunAsync(fixture, observer, receipt =>
        {
            Assert.True(checkedGap); Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var state = fixture.State;
            AndroidCollectedCodec.RequireResolved(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            Assert.Single(AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            fixture.VerifyOriginal();
        });
    }

    private static Task RunAsync(AndroidRuntimeFixture fixture, ITargetPersistenceObserver observer, Action<AttemptReceipt> inspect) =>
        fixture.ExecuteAsync(async () =>
        {
            var source = fixture.Source; var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
            var session = new AttemptRuntimeSession(source.Instance, source.Database, new(), source.Lease,
                fixture.Base.Resources, fixture.Base.Index, accepted, observer);
            Exception? original = null;
            try { var receipt = await new ExecutionAttemptCoordinator().RunAsync(session, CancellationToken.None); inspect(receipt); fixture.VerifyOriginal(); }
            catch (Exception error) { original = error; }
            try
            {
                if (!session.IsTerminal) await session.QuarantineFailureAsync();
                session.Dispose();
            }
            catch (Exception cleanup)
            {
                if (original is not null) throw new AggregateException(original, cleanup);
                throw;
            }
            if (original is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
        });

    private sealed class CaptureObserver(Action<TargetPersistencePoint> action) : ITargetPersistenceObserver
    {
        public void Reached(TargetPersistencePoint point, ExecutionTargetBinding binding) { }
        public void Reached(TargetPersistencePoint point, ExecutionCaptureBinding binding) => action(point);
    }
}
