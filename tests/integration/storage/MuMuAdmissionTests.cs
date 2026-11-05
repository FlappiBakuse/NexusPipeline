using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MuMuAdmissionTests
{
    [Theory(DisplayName = "Cancellation Or Failure After The Actual Child Root Never Acknowledges Or Resumes That Command")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnacknowledgedRoots(bool fail)
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            configuration: true, forceClose: false, captureMode: "rgba");
        var reached = false;
        var observer = new CaptureObserver(point =>
        {
            if (point != TargetPersistencePoint.CaptureCommandRootPublished || reached) return;
            reached = true;
            if (fail) throw new IOException("fixture_mumu_child_before_ack");
            fixture.Cancel();
        });
        await RunAsync(fixture, observer, receipt =>
        {
            Assert.True(reached);
            Assert.Equal(fail ? LifecycleOutcome.Completed : LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var saved = AndroidCaptureTests.ReadCapture(fixture);
            Assert.Equal("unavailable", saved.Result!.Outcome);
            var child = Assert.Single(saved.Commands);
            Assert.Equal(1, child.Root.Sequence); Assert.Null(child.Observation);
            var observed = MuMuRuntimeTests.ReadInvocations(fixture.Directory);
            try { Assert.DoesNotContain(observed, item => item.RootElement.GetProperty("processId").GetInt32() == child.Root.Process.ProcessId); }
            finally { foreach (var item in observed) item.Dispose(); }
            Assert.False(Directory.Exists(Path.Combine(fixture.ScreenshotDirectory!, "observed")));
            Assert.Null(saved.Result.Png); Assert.NotNull(saved.Parent);
            Assert.All(saved.Result.HelperStopped!.AllFamily,
                member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            fixture.VerifyOriginal();
        });
    }

    [Theory(DisplayName = "Accepted Cancellation After An Actual Observation Stops Every Later Mapping Or Image Effect")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopsAfterCancellation(int after)
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            configuration: true, forceClose: false, captureMode: "rgba");
        var observedCount = 0; var cancelled = false;
        var observer = new CaptureObserver(point =>
        {
            if (point != TargetPersistencePoint.CaptureCommandObservedPublished || ++observedCount != after) return;
            fixture.Cancel(); cancelled = true;
        });
        await RunAsync(fixture, observer, receipt =>
        {
            Assert.True(cancelled); Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var saved = AndroidCaptureTests.ReadCapture(fixture);
            Assert.Equal("unavailable", saved.Result!.Outcome); Assert.Equal(after, saved.Commands.Length);
            Assert.All(saved.Commands, command => Assert.Equal("natural-root-exit-and-eof-before-stop", command.Observation!.Completion));
            Assert.DoesNotContain(saved.Commands, command => command.Root.Sequence == 3);
            var screenshotInvocations = Path.Combine(fixture.ScreenshotDirectory!, "observed");
            Assert.Equal(after == 2, Directory.Exists(screenshotInvocations));
            if (after == 2) Assert.Single(Directory.GetFiles(screenshotInvocations, "*.json"));
            Assert.Null(saved.Result.Png); Assert.Null(saved.Result.Image);
            Assert.All(saved.Result.HelperStopped!.AllFamily,
                member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            var state = fixture.State;
            AndroidCollectedCodec.RequireResolved(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            fixture.VerifyOriginal();
        });
    }

    [Fact(DisplayName = "An Expired Child Admission Stops The Actual Suspended Family Outside Its Ownership And Publication Gates")]
    public async Task StopsExpiredAdmission()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            launch: false, forceClose: false, captureMode: "rgba");
        var reached = false;
        var observer = new CaptureObserver(point =>
        {
            if (point != TargetPersistencePoint.CaptureCommandRootPublished || reached) return;
            reached = true;
            Thread.Sleep(TimeSpan.FromSeconds(6));
        });
        await RunAsync(fixture, observer, receipt =>
        {
            Assert.True(reached); Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var saved = AndroidCaptureTests.ReadCapture(fixture);
            Assert.Equal("unavailable", saved.Result!.Outcome);
            Assert.Equal("execution_capture_deadline", saved.Result.ReasonCode);
            var child = Assert.Single(saved.Commands);
            Assert.Equal(1, child.Root.Sequence); Assert.Null(child.Observation);
            Assert.Null(saved.Result.Png); Assert.Null(saved.Result.Image); Assert.NotNull(saved.Parent);
            var invocations = MuMuRuntimeTests.ReadInvocations(fixture.Directory);
            try { Assert.DoesNotContain(invocations, item => item.RootElement.GetProperty("processId").GetInt32() == child.Root.Process.ProcessId); }
            finally { foreach (var item in invocations) item.Dispose(); }
            Assert.False(Directory.Exists(Path.Combine(fixture.ScreenshotDirectory!, "observed")));
            Assert.All(saved.Result.HelperStopped!.AllFamily,
                member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            fixture.VerifyOriginal();
        }).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static Task RunAsync(AndroidRuntimeFixture fixture, ITargetPersistenceObserver observer,
        Action<AttemptReceipt> inspect) => fixture.ExecuteAsync(async () =>
    {
        var source = fixture.Source; var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        var session = new AttemptRuntimeSession(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index, accepted, observer);
        Exception? original = null;
        try { var receipt = await new ExecutionAttemptCoordinator().RunAsync(session, CancellationToken.None); inspect(receipt); }
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
