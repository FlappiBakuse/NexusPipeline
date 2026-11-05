using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StepLimitsTests
{
    [Theory(DisplayName = "Real Monotonic Step Limits Use Actual Bytes And Continue The Frozen Queue")]
    [InlineData("wait", 1, 30, false, "execution_step_stall_timeout")]
    [InlineData("first-partial-timeout", 1, 4, false, BindingTimeoutCodec.Reason)]
    [InlineData("partial-activity", 1, 5, false, null)]
    [InlineData("first-silent-timeout", 1, 4, true, "execution_step_stall_timeout")]
    public async Task EnforcesMonotonicLimits(string mode, int stall, int total, bool fileLogs, string? expectedStop)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: fileLogs, mode: mode, limits: new(1, stall, total), independentSuccessors: true);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync()); var source = fixture.Source;
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var state = fixture.State;
        Assert.Equal(3, state.Steps.Length); Assert.All(state.Coverage, item => Assert.Equal(StepCompletionKind.Collected, item.Kind));
        Assert.Equal(3 + state.Attempt.ConfigurationPlans.Count, fixture.Base.Count("journal_writer_job_roots"));
        var closures = new JobClosureStore(source.Instance, source.Database, new());
        var actualRoots = state.Steps.Select(job => closures.ReadForExecutionEvidence(state.Journal.JournalId, job.WriterId, job.JobName, job.SessionId)!).ToArray();
        Assert.All(actualRoots, closure => { Assert.True(closure.WasExecuted); Assert.NotNull(closure.Root); });
        Assert.Equal(3, actualRoots.DistinctBy(closure => (closure.JobName, closure.Root!.ProcessId, closure.Root!.CreatedAtUtc)).Count());
        foreach (var child in state.Attempt.ConfigurationPlans)
        {
            Assert.Equal(state.Total.Steps[0].StepId, child.StepId);
            var plan = new ConfigurationPlanCodec(source.Instance, new(), new(source.Instance, new())).ReadStructure(AttemptPlanCodec.Reference(child.Plan));
            var primary = actualRoots[0];
            var stored = Assert.IsType<FrozenJobClosure>(closures.Read(plan.JournalId, primary.WriterId, primary.JobName, primary.SessionId));
            Assert.Equal(primary.Root, stored.Root); Assert.True(primary.Family.SequenceEqual(stored.Family));
        }
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var ordinal = 0;
        foreach (var result in StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total))
        {
            var stopForStep = ordinal++ == 0 ? expectedStop : null;
            var document = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
            if (stopForStep == BindingTimeoutCodec.Reason)
            {
                var timeout = Assert.Single(BindingTimeoutCodec.Read(state.Latest.Root, state.Total));
                Assert.Equal(result.Binding.StepId, timeout.StepId);
                Assert.True(timeout.ConsumedMilliseconds >= total * 1000);
                var fact = ExecutionBindingRoles.Combine(state.Latest.Root, state.Total,
                    state.Coverage.Single(item => item.StepId == timeout.StepId));
                Assert.Equal(BindingTimeoutCodec.Reason, fact.ReasonCode);
                Assert.Equal(EngineOutcome.Failed, fact.Engine);
                Assert.False(ExecutionInvocationStore.CanRetry(state, timeout.StepId));
                Assert.True(RawOutputCodec.Read(blobs, result.Binding, document.RawOutput!).ObservedBytes > 0);
                continue;
            }
            Assert.Equal(stopForStep, document.Stop?.ReasonCode); Assert.Equal(BusinessOutcome.Unverified, document.Business);
            Assert.Equal(stopForStep is null ? EngineOutcome.Failed : EngineOutcome.Unknown, document.Engine);
            Assert.Equal(result.Latest.Artifact, new StepEvidenceReader(source.Instance, source.Database, new()).Read(result.Binding, result.Latest.Artifact, true).Artifact);
            if (document.Stop is { } stop)
            {
                Assert.Equal(expectedStop, document.ReasonCode);
                Assert.True(stop.ElapsedMilliseconds >= (expectedStop == "execution_step_total_timeout" ? total : stall) * 1000);
                var raw = RawOutputCodec.Read(blobs, result.Binding, document.RawOutput!);
                Assert.True(raw.IsComplete); Assert.True(stop.ObservedBytes <= raw.ObservedBytes);
                if (mode == "first-partial-timeout") Assert.True(stop.ElapsedMilliseconds - stop.LastOutputElapsedMilliseconds < 1000);
            }
        }
        Assert.True(fixture.Base.Index.Current.IsReady); Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
    }

    [Fact(DisplayName = "A Real Root Exiting Inside Its Success Grace Keeps Its Actual Exit And Runs The Next Binding")]
    public async Task PreservesNaturalExit()
    {
        using var fixture = new ExecutionRuntimeFixture(judgement: new(AutomationJudgementLanguage.Keywords, ["ready"], [], null));
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business); Assert.Equal(3, fixture.Base.Count("journal_writer_job_roots"));
        var source = fixture.Source;
        foreach (var result in StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total))
        {
            var document = StepEvidenceCodec.Read(new(source.Instance, new()), result.Binding, result.Latest.Artifact);
            Assert.Null(document.Stop); Assert.Equal(37, document.ExitCode); Assert.Equal(BusinessOutcome.Succeeded, document.Business);
        }
    }

    [Fact(DisplayName = "A Real Failure Marker Stops Only Its Binding And Continues The Frozen Queue")]
    public async Task ContinuesFailedBindings()
    {
        using var fixture = new ExecutionRuntimeFixture(mode: "wait", judgement: new(AutomationJudgementLanguage.Keywords, [], ["ready"], null));
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(BusinessOutcome.Failed, receipt.Facts.Business);
        Assert.Equal(EngineOutcome.Unknown, receipt.Facts.Engine); Assert.Equal(3, fixture.Base.Count("journal_writer_job_roots"));
        foreach (var result in StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total))
        {
            var document = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), result.Binding, result.Latest.Artifact);
            Assert.Equal("execution_business_failed", document.Stop!.ReasonCode); Assert.Equal(BusinessOutcome.Failed, document.Business);
        }
    }

    [Fact(DisplayName = "Accepted Cancellation Takes Priority Over A Local Stall And Never Dispatches The Suffix")]
    public async Task PrioritizesAcceptedCancellation()
    {
        using var fixture = new ExecutionRuntimeFixture(mode: "wait", limits: new(1, 2, 30));
        var running = fixture.Consumer.RunNextAsync(); await fixture.Ready(".output-ready"); fixture.Cancel();
        var receipt = Assert.IsType<AttemptReceipt>(await running);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(1, fixture.Base.Count("journal_writer_job_roots"));
        var result = Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        var document = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), result.Binding, result.Latest.Artifact);
        Assert.Null(document.Stop); Assert.Equal(BusinessOutcome.Unverified, document.Business);
        Assert.All(fixture.State.Coverage.Skip(1), item => Assert.Equal(StepCompletionKind.NotDispatched, item.Kind));
    }

    [Theory(DisplayName = "A Collected Timeout Cannot Be Rewritten Past Its Native Closure Or To A Different Frozen Limit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsCollectedTimeout(bool changeDuration)
    {
        using var fixture = new ExecutionRuntimeFixture(mode: "wait", limits: new(1, 1, 30));
        _ = await fixture.Consumer.RunNextAsync(); var source = fixture.Source;
        var state = fixture.State; var result = StepResultCodec.Read(state.Latest.Root, state.Total)[0];
        var document = StepEvidenceCodec.Read(new(source.Instance, new()), result.Binding, result.Latest.Artifact);
        var original = document.Stop!;
        var forged = changeDuration ? original with { ElapsedMilliseconds = state.Steps[0].ClosedElapsedMilliseconds!.Value + 1 } :
            original with { ReasonCode = "execution_step_total_timeout" };
        var artifact = StepEvidenceCodec.Write(new(source.Instance, new()), document with { Stop = forged });
        var before = source.Snapshot();
        Assert.Throws<IOException>(() => new StepEvidenceReader(source.Instance, source.Database, new()).Read(result.Binding, artifact, true));
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }
}
