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
public sealed class CaptureFaultTests
{
    [Theory(DisplayName = "An Accepted Cancel After The Actual Capture Source Prevents Helper Root Admission And Resume")]
    [InlineData(1)]
    [InlineData(200)]
    public async Task CancelsSourceAdmission(int captureFrames)
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true, captureFrames: captureFrames);
        var observer = new CaptureObserver((point, _) =>
        {
            if (point == TargetPersistencePoint.CaptureSourcePublished) fixture.Cancel();
        });
        await fixture.ExecuteAsync(() => WithSession(fixture, observer, async (session, _) =>
        {
            var receipt = await new ExecutionAttemptCoordinator().RunAsync(session, CancellationToken.None);
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            Assert.Equal(1, observer.SourceCount); Assert.Equal(0, observer.RootCount);
            var saved = ReadCapture(fixture);
            Assert.Null(saved.Root); Assert.Null(saved.State.Root);
            Assert.Equal("unavailable", saved.Result!.Outcome); Assert.Null(saved.Result.Png);
            Assert.Null(saved.Result.Frames);
            Assert.NotNull(saved.Result.HelperStopped);
            Assert.Equal(0U, saved.Result.HelperStopped!.TotalProcesses);
            Assert.Empty(saved.Result.HelperStopped.AllFamily);
            RequireStoppedParent(saved);
            Assert.Equal(DispatchIntentCodec.Reason, fixture.State.Cancellation!.ReasonCode);
        }));
    }

    [Theory(DisplayName = "A Real Suspended Capture Root Whose Published Observer Throws Is Collected In The Original Parent Family")]
    [InlineData(1)]
    [InlineData(200)]
    public async Task CollectsObserverFailures(int captureFrames)
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true, captureFrames: captureFrames);
        ProcessIdentity? suspended = null;
        var observer = new CaptureObserver((point, _) =>
        {
            if (point != TargetPersistencePoint.CaptureRootPublished) return;
            suspended = Assert.IsType<CaptureRootDocument>(ReadCapture(fixture, insidePublication: true).Root).NativeRoot;
            Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(suspended));
            throw new IOException("fixture_capture_before_resume");
        });
        await fixture.ExecuteAsync(() => WithSession(fixture, observer, async (session, _) =>
        {
            var receipt = await new ExecutionAttemptCoordinator().RunAsync(session, CancellationToken.None);
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(1, observer.SourceCount); Assert.Equal(1, observer.RootCount);
            var saved = ReadCapture(fixture);
            Assert.NotNull(suspended); Assert.Equal(suspended, saved.Root!.NativeRoot);
            Assert.Equal("unavailable", saved.Result!.Outcome); Assert.Null(saved.Result.Png);
            Assert.Null(saved.Result.Frames);
            var inner = Assert.IsType<OwnedTargetInventory>(saved.Result.HelperStopped);
            Assert.Equal(1U, inner.TotalProcesses); Assert.Equal(0U, inner.ActiveProcesses);
            Assert.Equal(suspended, Assert.Single(inner.AllFamily));
            Assert.Contains(suspended!, saved.Parent!.Family);
            Assert.All(inner.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            RequireStoppedParent(saved);
        }));
    }

    [Theory(DisplayName = "A Captured Append With Changed Prior Bytes Or Failed Sql Projection Cannot Open Before Reconciliation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcilesCapturedBytes(bool replacePriorBytes)
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true);
        var installed = false;
        BlobReference? artifact = null, png = null;
        byte[]? originalPng = null;
        byte[]? originalRevision = null;
        var revisionPath = Path.Combine(fixture.Source.Instance.RuntimeRoot, "journals", fixture.Accepted.JournalId.ToString("D"), "0000000001.json");
        var observer = new CaptureObserver((point, _) =>
        {
            if (point != TargetPersistencePoint.CaptureCollectedAppended || installed) return;
            var saved = ReadCapture(fixture, insidePublication: true);
            Assert.Equal("captured", saved.Result!.Outcome);
            artifact = saved.State.Result; png = saved.Result.Png;
            Assert.NotNull(artifact); Assert.NotNull(png);
            originalPng = File.ReadAllBytes(ContentPath(fixture, png!));
            Assert.ThrowsAny<IOException>(() => new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                .OpenCaptured(saved.State.Binding, artifact!));
            if (replacePriorBytes)
            {
                originalRevision = File.ReadAllBytes(revisionPath);
                File.WriteAllBytes(revisionPath, [.. originalRevision, (byte)'\n']);
            }
            else fixture.Base.Execute("CREATE TRIGGER fixture_capture_advance_failure BEFORE UPDATE OF revision ON operation_journals " +
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
                if (replacePriorBytes)
                {
                    var changed = await Assert.ThrowsAsync<IOException>(() => session.ExecuteStepAsync(step, CancellationToken.None));
                    Assert.Equal("execution_capture_original_prefix_unbound", changed.Message);
                }
                else await Assert.ThrowsAsync<SqliteException>(() => session.ExecuteStepAsync(step, CancellationToken.None));
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
                if (installed)
                {
                    if (replacePriorBytes) File.WriteAllBytes(revisionPath, originalRevision!);
                    else fixture.Base.Execute("DROP TRIGGER fixture_capture_advance_failure;");
                    installed = false;
                }
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

    [Fact(DisplayName = "Exhausting The Original Accepted Budget At The Published Source Cannot Admit Or Resume The Capture Root")]
    public async Task EnforcesAcceptedBudget()
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true, totalBudgetSeconds: 120);
        ExecutionTimeBudget? actualBudget = null;
        var observer = new CaptureObserver((point, _) =>
        {
            if (point != TargetPersistencePoint.CaptureSourcePublished) return;
            var actual = Assert.IsType<ExecutionTimeBudget>(actualBudget);
            var remaining = actual.Read().Remaining;
            Assert.True(remaining > TimeSpan.Zero); Assert.True(remaining <= TimeSpan.FromSeconds(120));
            var elapsed = Stopwatch.StartNew();
            while (actual.Read().Remaining > TimeSpan.Zero)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(121)) throw new IOException("fixture_actual_budget_did_not_expire");
                Thread.Sleep(20);
            }
            Assert.True(actual.Read().IsExhausted);
        });
        await fixture.ExecuteAsync(() => WithSession(fixture, observer, async (session, accepted) =>
        {
            actualBudget = accepted.Prepared.TimeBudget;
            Assert.Equal(120, accepted.Prepared.Total.Plan.ReadWire().Budget.TotalTimeoutSeconds);
            var receipt = await new ExecutionAttemptCoordinator().RunAsync(session, CancellationToken.None);
            Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
            Assert.Equal(DispatchIntentCodec.BudgetReason, receipt.Facts.ReasonCode);
            Assert.True(actualBudget.Read().IsExhausted);
            Assert.Equal(1, observer.SourceCount); Assert.Equal(0, observer.RootCount);
            var saved = ReadCapture(fixture);
            Assert.Null(saved.Root); Assert.Null(saved.State.Root); Assert.Null(saved.Result!.Png);
            Assert.Equal("unavailable", saved.Result.Outcome);
            Assert.Equal("execution_capture_output_incomplete", saved.Result.ReasonCode);
            Assert.NotNull(saved.Result.HelperStopped);
            Assert.All(saved.Result.HelperStopped!.AllFamily, member =>
                Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            RequireStoppedParent(saved);
        }));
    }

    private static async Task WithSession(PcStageFixture fixture, CaptureObserver observer,
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

    private static CaptureSavedEvidence ReadCapture(PcStageFixture fixture, bool insidePublication = false)
    {
        var state = insidePublication
            ? AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Accepted.JournalId)
            : fixture.State;
        return Assert.Single(new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadMetadata(state.Journal, state.Attempt, state.Total,
                new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId)));
    }

    private static string ContentPath(PcStageFixture fixture, BlobReference reference) =>
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
