using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CaptureContinuationTests
{
    [Fact(DisplayName = "A Physically Appended Generic Rgba Frame Requires Its Actual Sql Projection And Late Cancellation Preserves Every Original Byte")]
    public async Task PreservesCapturedProjection()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, capture: true, captureMode: "rgba");
        var installed = false;
        BlobReference? artifact = null, png = null;
        byte[]? originalPng = null;
        var observer = new CaptureObserver((point, _) =>
        {
            if (point != TargetPersistencePoint.CaptureCollectedAppended || installed) return;
            var saved = ReadCapture(fixture);
            Assert.Equal("captured", saved.Result!.Outcome);
            artifact = saved.State.Result; png = saved.Result.Png;
            Assert.NotNull(artifact); Assert.NotNull(png);
            originalPng = File.ReadAllBytes(ContentPath(fixture, png!));
            Assert.ThrowsAny<IOException>(() => new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                .OpenCaptured(saved.State.Binding, artifact!));
            fixture.Base.Execute("CREATE TRIGGER main.fixture_capture_advance_failure BEFORE UPDATE OF revision ON operation_journals " +
                $"WHEN OLD.journal_id='{fixture.Accepted.JournalId:D}' BEGIN SELECT RAISE(ABORT,'fixture_capture_advance_failure'); END;");
            installed = true;
        });
        await fixture.ExecuteAsync(() => WithSession(fixture, observer, async (session, _) =>
        {
            try
            {
                session.PrepareConfigurationGroups();
                var step = Assert.Single(session.StepIds);
                session.PrepareNextStep(step); session.EnterEffect(step);
                await Assert.ThrowsAsync<SqliteException>(() => session.ExecuteStepAsync(step, CancellationToken.None));
                Assert.True(installed); Assert.NotNull(artifact); Assert.NotNull(png);
                var ahead = fixture.State;
                Assert.Equal(ahead.Journal.Revision + 1, ahead.Latest.Revision);
                Assert.ThrowsAny<IOException>(() => new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                    .OpenCaptured(ReadCapture(fixture).State.Binding, artifact!));
                var operations = fixture.Base.Count("operations");
                var rejection = Assert.Throws<IOException>(() => fixture.Cancel());
                Assert.Equal("execution_cancel_parent_unavailable", rejection.Message);
                Assert.Equal(operations, fixture.Base.Count("operations"));
            }
            finally
            {
                if (installed) { fixture.Base.Execute("DROP TRIGGER main.fixture_capture_advance_failure;"); installed = false; }
            }
            fixture.Source.Lease.PublishExecution(() =>
            {
                ExecutionAttemptProjection.Synchronize(fixture.Source.Instance, fixture.Source.Database, new(),
                    AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Accepted.JournalId));
            });
            var projected = ReadCapture(fixture);
            Assert.Equal(artifact, projected.State.Result);
            using (var opened = new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                .OpenCaptured(projected.State.Binding, artifact!))
                Assert.Equal(png!.SizeBytes, opened.Length);
            fixture.Cancel();
            session.FreezeDispatch(ExecutionStopReason.Cancelled);
            await session.CollectRemainingAsync();
            session.CloseUndispatched(); session.MarkApplied();
            await session.RestoreRemainingAsync(ExecutionStopReason.Cancelled);
            var receipt = session.Commit(ExecutionStopReason.Cancelled);
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var final = ReadCapture(fixture);
            Assert.Equal(artifact, final.State.Result); Assert.Equal(png, final.Result!.Png);
            Assert.Equal("captured", final.Result.Outcome);
            Assert.Equal(originalPng, File.ReadAllBytes(ContentPath(fixture, png!)));
            Assert.Equal(1, observer.CollectedCount);
            RequireStoppedParent(final);
        }));
    }

    private static async Task WithSession(AndroidRuntimeFixture fixture, CaptureObserver observer,
        Func<AttemptRuntimeSession, AcceptedExecutionAdmission, Task> body)
    {
        var source = fixture.Source;
        var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        var session = new AttemptRuntimeSession(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index, accepted, observer);
        Exception? original = null;
        try { await body(session, accepted); }
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
    }

    private static CaptureSavedEvidence ReadCapture(AndroidRuntimeFixture fixture)
    {
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Accepted.JournalId);
        return Assert.Single(new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadMetadata(state.Journal, state.Attempt, state.Total,
                new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId)));
    }

    private static string ContentPath(AndroidRuntimeFixture fixture, BlobReference reference) =>
        Path.Combine(fixture.Source.Instance.RuntimeRoot, reference.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void RequireStoppedParent(CaptureSavedEvidence saved)
    {
        var parent = Assert.IsType<FrozenJobClosure>(saved.Parent);
        Assert.True(parent.WasExecuted); Assert.NotNull(parent.Root);
        Assert.Equal(saved.State.Binding.Primary.JobName, parent.JobName);
        Assert.Equal(saved.State.Binding.Primary.WriterId, parent.WriterId);
        Assert.All(parent.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.All(saved.Result!.HelperStopped!.AllFamily, member => Assert.Contains(member, parent.Family));
    }

    private sealed class CaptureObserver(Action<TargetPersistencePoint, ExecutionCaptureBinding> observe)
        : ITargetPersistenceObserver
    {
        internal int SourceCount { get; private set; }
        internal int RootCount { get; private set; }
        internal int CollectedCount { get; private set; }
        public void Reached(TargetPersistencePoint point, ExecutionTargetBinding binding) { }
        public void Reached(TargetPersistencePoint point, ExecutionCaptureBinding binding)
        {
            if (point == TargetPersistencePoint.CaptureSourcePublished) SourceCount++;
            if (point == TargetPersistencePoint.CaptureRootPublished) RootCount++;
            if (point == TargetPersistencePoint.CaptureCollectedAppended) CollectedCount++;
            observe(point, binding);
        }
    }
}
