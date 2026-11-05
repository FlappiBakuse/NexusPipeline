using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StopPublicationTests
{
    [Theory(DisplayName = "A Real Collected Stop And Accepted Cancellation Keep Their Winning Order Across An Unwritten Close")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OrdersStopCancellation(
        bool knownBusiness, bool cancellationFirst)
    {
        using var fixture = new StepWriterFixture("wait", judgement: new(AutomationJudgementLanguage.Keywords,
            knownBusiness ? [] : ["done"], knownBusiness ? ["ready"] : [], null), limits: new(1, 1, 120));
        var source = fixture.Base.Source;
        var originalIdentity = PhysicalObjectIdentity.Read(source.Configuration); var originalBytes = File.ReadAllBytes(source.Configuration);
        var state = fixture.Scope.VerifyReadOnly(); var binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); var root = collector.Start();
        await RequestRealStop(collector, fixture.Marker, knownBusiness);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: true);
        Assert.Equal(root.ProcessId, stopped.Closure.Root!.ProcessId);
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        var evidence = proof.Use(binding, value => value); var blobs = new ImmutableBlobStore(source.Instance, new());
        var original = StepEvidenceCodec.Read(blobs, binding, evidence.Artifact);
        Assert.Equal(knownBusiness ? "execution_business_failed" : "execution_step_stall_timeout", original.Stop!.ReasonCode);
        var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
        if (!cancellationFirst) terminal.RecordCollected(fixture.JournalId, proof);

        state = fixture.Scope.VerifyReadOnly(); var revisionBeforeCancel = state.Latest.Revision;
        var cancel = ExecutionCancellationStore.Identity(Guid.NewGuid(), "step-writer-fixture", fixture.JournalId,
            state.Journal.OperationId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
        Assert.Throws<IOException>(() => new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease,
            new FailAfterAccepted()).Cancel(cancel, fixture.JournalId, DateTimeOffset.UtcNow));
        state = fixture.Scope.VerifyReadOnly(); Assert.Equal("accepted", state.Cancellation!.Status);
        Assert.Equal(cancel, state.Cancellation.Identity); Assert.Equal(revisionBeforeCancel, state.Latest.Revision);
        Assert.Null(DispatchIntentCodec.Read(state.Latest.Root, state.Total));
        terminal.RecordCollected(fixture.JournalId, proof);
        var published = Published(fixture);
        Assert.Equal(knownBusiness ? BusinessOutcome.Failed : BusinessOutcome.Unverified, published.Document.Business);
        Assert.Equal(original.Engine, published.Document.Engine); Assert.Equal(original.RawOutput, published.Document.RawOutput);
        Assert.Equal(original.Root, published.Document.Root); Assert.Equal(original.Closure, published.Document.Closure);
        Assert.Equal(original.PreviousObservation, published.Document.PreviousObservation);
        if (cancellationFirst)
        {
            Assert.Null(published.Document.Stop); Assert.NotEqual(evidence.Artifact, published.Artifact);
            Assert.Equal(knownBusiness ? "judgement_keyword_failed" : "execution_output_unverified", published.Document.ReasonCode);
        }
        else { Assert.Equal(original, published.Document); Assert.Equal(evidence.Artifact, published.Artifact); }
        Assert.Equal(original, StepEvidenceCodec.Read(blobs, binding, evidence.Artifact));
        var before = source.Snapshot(); terminal.RecordCollected(fixture.JournalId, proof);
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Equal(published.Artifact, Published(fixture).Artifact);

        var resumed = new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease)
            .Cancel(cancel, fixture.JournalId, DateTimeOffset.UtcNow);
        Assert.False(resumed.Acceptance.IsNew); Assert.True(resumed.IsProjected);
        terminal.CloseUndispatched(fixture.JournalId); terminal.MarkApplied(fixture.JournalId);
        var receipt = terminal.Commit(fixture.JournalId, LifecycleOutcome.Cancelled, "execution_cancel_requested");
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(knownBusiness ? BusinessOutcome.Failed : BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(1, fixture.Base.Count("journal_writer_jobs")); Assert.Equal(1, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(1, fixture.Base.Count("journal_writer_job_closures")); Assert.Equal(1, fixture.Base.Count("attempt_receipts"));
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        Assert.Equal(originalIdentity, PhysicalObjectIdentity.Read(source.Configuration)); Assert.Equal(originalBytes, File.ReadAllBytes(source.Configuration));
    }

    [Fact(DisplayName = "An Unpublished None Mode Collection Cannot Add Business After Cancellation Wins The Sql Boundary")]
    public async Task RejectsLateBusiness()
    {
        using var fixture = new StepWriterFixture("wait", judgement: new(AutomationJudgementLanguage.Keywords, [], [], null));
        var source = fixture.Base.Source; var state = fixture.Scope.VerifyReadOnly();
        var binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (!File.Exists(fixture.Marker + ".output-ready")) await Task.Delay(10, timeout.Token);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: true);
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10)); var original = proof.Use(binding, value => value);
        Assert.Equal(BusinessOutcome.NotApplicable, original.Business);
        state = fixture.Scope.VerifyReadOnly(); Assert.Empty(StepResultCodec.Read(state.Latest.Root, state.Total));
        var cancel = ExecutionCancellationStore.Identity(Guid.NewGuid(), "step-writer-fixture", fixture.JournalId,
            state.Journal.OperationId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
        Assert.Throws<IOException>(() => new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease,
            new FailAfterAccepted()).Cancel(cancel, fixture.JournalId, DateTimeOffset.UtcNow));
        state = fixture.Scope.VerifyReadOnly(); Assert.Equal("accepted", state.Cancellation!.Status);
        Assert.Null(DispatchIntentCodec.Read(state.Latest.Root, state.Total));
        var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
        terminal.RecordCollected(fixture.JournalId, proof); var published = Published(fixture);
        Assert.Equal(BusinessOutcome.Unverified, published.Document.Business); Assert.Null(published.Document.Stop);
        Assert.Equal(original.Engine, published.Document.Engine);
        var originalDocument = StepEvidenceCodec.Read(new(source.Instance, new()), binding, original.Artifact);
        Assert.Equal(BusinessOutcome.NotApplicable, originalDocument.Business);
        Assert.Equal(originalDocument.RawOutput, published.Document.RawOutput); Assert.Equal(originalDocument.Closure, published.Document.Closure);
        var before = source.Snapshot(); terminal.RecordCollected(fixture.JournalId, proof);
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease).Cancel(cancel, fixture.JournalId, DateTimeOffset.UtcNow);
        terminal.CloseUndispatched(fixture.JournalId); terminal.MarkApplied(fixture.JournalId);
        var receipt = terminal.Commit(fixture.JournalId, LifecycleOutcome.Cancelled, "execution_cancel_requested");
        Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business); Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }

    private static async Task RequestRealStop(StepEvidenceCollector collector, string marker, bool knownBusiness)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (knownBusiness) Assert.Equal(BusinessOutcome.Failed, (await collector.BusinessAccepted.WaitAsync(timeout.Token)).Outcome);
        else while (!File.Exists(marker + ".output-ready")) await Task.Delay(10, timeout.Token);
        var clock = Stopwatch.StartNew();
        while (!collector.TryRequestStop() && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, timeout.Token);
        Assert.True(collector.TryRequestStop());
    }

    private static (BlobReference Artifact, StepEvidenceDocument Document) Published(StepWriterFixture fixture)
    {
        var state = fixture.Scope.VerifyReadOnly(); var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        Assert.True(result.IsCollected);
        return (result.Latest.Artifact, StepEvidenceCodec.Read(new(fixture.Base.Source.Instance, new()), result.Binding, result.Latest.Artifact));
    }

    private sealed class FailAfterAccepted : IExecutionCancellationObserver
    {
        public void Reached(ExecutionCancellationPoint point)
        {
            if (point == ExecutionCancellationPoint.IntentAccepted) throw new IOException("fixture_cancel_accepted_before_close");
        }
    }
}
