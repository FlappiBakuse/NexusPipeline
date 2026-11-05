using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidFaultTests
{
    [Theory(DisplayName = "Accepted Source Cancellation Or A Suspended Root Observer Failure Never Resumes The Generic Capture Tool")]
    [InlineData("sourceCancel")]
    [InlineData("rootFailure")]
    public async Task RejectsCancelledSources(string boundary)
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, capture: true, captureMode: "rgba");
        var reached = false;
        var observer = new CaptureObserver(point =>
        {
            if (boundary == "sourceCancel" && point == TargetPersistencePoint.CaptureSourcePublished)
            { reached = true; fixture.Cancel(); }
            if (boundary == "rootFailure" && point == TargetPersistencePoint.CaptureRootPublished)
            { reached = true; throw new IOException("fixture_capture_before_resume"); }
        });
        await fixture.ExecuteAsync(async () =>
        {
            var source = fixture.Source;
            var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
            var session = new AttemptRuntimeSession(source.Instance, source.Database, new(), source.Lease,
                fixture.Base.Resources, fixture.Base.Index, accepted, observer);
            Exception? original = null;
            try
            {
                var receipt = await new ExecutionAttemptCoordinator().RunAsync(session, CancellationToken.None);
                Assert.True(reached);
                Assert.Equal(boundary == "sourceCancel" ? LifecycleOutcome.Cancelled : LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
                var saved = AndroidCaptureTests.ReadCapture(fixture);
                Assert.Equal("unavailable", saved.Result!.Outcome); Assert.Null(saved.Result.Png); Assert.Null(saved.Result.Image);
                Assert.NotNull(saved.Result.HelperStopped); Assert.NotNull(saved.Parent);
                Assert.All(saved.Result.HelperStopped!.AllFamily, member =>
                {
                    Assert.Contains(member, saved.Parent!.Family);
                    Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member));
                });
                if (boundary == "sourceCancel") Assert.Null(saved.Root);
                else Assert.NotNull(saved.Root);
                Assert.False(File.Exists(Path.Combine(fixture.Directory, "fixture-screenshot.png")));
                foreach (var path in Directory.GetFiles(Path.Combine(fixture.Directory, "observed"), "*.json"))
                {
                    using var row = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
                    Assert.DoesNotContain(row.RootElement.GetProperty("arguments").EnumerateArray(), value => value.GetString() == "exec-out");
                }
                fixture.VerifyOriginal();
            }
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
    }

    [Theory(DisplayName = "Actual Generic Captured Sources And Sql Image Facts Cannot Be Rebound Or Partially Projected")]
    [InlineData("rollback")]
    [InlineData("dimensions")]
    [InlineData("source")]
    public async Task RejectsReboundImages(string fault)
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, capture: true, captureMode: "rgba");
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync(); var source = fixture.Source;
            var saved = AndroidCaptureTests.ReadCapture(fixture);
            var journal = new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export();
            var projection = new ImageCatalogProjection(source.Instance, source.Database, new(), source.Lease);
            if (fault == "rollback")
            {
                fixture.Base.Execute("CREATE TRIGGER fixture_generic_image BEFORE INSERT ON execution_image_entries " +
                    "BEGIN SELECT RAISE(ABORT,'fixture_generic_image'); END;");
                try
                {
                    Assert.Throws<SqliteException>(() => projection.OnCommitted(receipt));
                    Assert.Equal(0, fixture.Base.Count("execution_image_catalog"));
                    Assert.Equal(0, fixture.Base.Count("execution_image_entries"));
                }
                finally { fixture.Base.Execute("DROP TRIGGER fixture_generic_image;"); }
            }
            projection.OnCommitted(receipt);
            var reader = new ImageCatalogReader(source.Instance, source.Database, new());
            var image = Assert.Single(reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            if (fault == "dimensions")
            {
                fixture.Base.Execute($"UPDATE execution_image_entries SET width=width+1 WHERE attempt_id='{receipt.Facts.AttemptId:D}';");
                Assert.ThrowsAny<IOException>(() => reader.ReadPage(new(image.Key.RunId, image.Key.AttemptId)));
                Assert.ThrowsAny<IOException>(() => reader.OpenOriginal(image.Key, image.ResultSha256));
            }
            else if (fault == "source")
            {
                var path = Path.Combine(source.Instance.RuntimeRoot, saved.State.Source.RelativePath);
                var original = File.ReadAllBytes(path);
                var node = JsonNode.Parse(original)!.AsObject(); node["android"]!["endpoint"] = "127.0.0.1:16385";
                File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node));
                try
                {
                    Assert.ThrowsAny<IOException>(() => reader.ReadPage(new(image.Key.RunId, image.Key.AttemptId)));
                    Assert.ThrowsAny<IOException>(() => reader.OpenOriginal(image.Key, image.ResultSha256));
                }
                finally { File.WriteAllBytes(path, original); }
            }
            else
            {
                using var input = reader.OpenOriginal(image.Key, image.ResultSha256);
                Assert.Equal(image.SizeBytes, input.Length);
                Assert.Equal(1, fixture.Base.Count("execution_image_catalog"));
            }
            Assert.Equal(journal, new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export());
            Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
        });
    }

    private sealed class CaptureObserver(Action<TargetPersistencePoint> action) : ITargetPersistenceObserver
    {
        public void Reached(TargetPersistencePoint point, ExecutionTargetBinding binding) { }
        public void Reached(TargetPersistencePoint point, ExecutionCaptureBinding binding) => action(point);
    }
}
