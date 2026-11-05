using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExecutionPublicationTests
{
    [Theory(DisplayName = "An Accepted Cancellation And A Real Business Publication Have One Order Without Leaving An Unprojected Success")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdersBusinessPublication(bool cancellationWins)
    {
        using var fixture = new StepWriterFixture("tail", judgement: new(AutomationJudgementLanguage.Keywords, ["ready"], [], null));
        var source = fixture.Base.Source; var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
        using var observed = await ObserveRealOutput(fixture); var proof = observed.Proof;
        using var accepted = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var observer = new Observer(() =>
        {
            accepted.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("fixture_publication_timeout");
        });
        if (!cancellationWins) terminal.RecordObservation(fixture.JournalId, proof);
        var cancellation = Task.Run(() => Cancel(fixture, observer));
        Task? result = null;
        try
        {
            Assert.True(accepted.Wait(TimeSpan.FromSeconds(10)));
            var pending = fixture.Scope.VerifyReadOnly(); Assert.Equal("accepted", pending.Cancellation!.Status);
            Assert.Equal(cancellationWins ? 0 : 1, StepResultCodec.Read(pending.Latest.Root, pending.Total).Length);
            if (cancellationWins)
            {
                result = Task.Run(() => terminal.RecordObservation(fixture.JournalId, proof));
                await WaitCount(source.Lease, 2); Assert.False(result.IsCompleted);
            }
        }
        finally { release.Set(); }
        _ = await cancellation.WaitAsync(TimeSpan.FromSeconds(10));
        if (result is not null)
            Assert.Equal("execution_judgement_admission_closed", (await Assert.ThrowsAsync<IOException>(async () =>
                await result.WaitAsync(TimeSpan.FromSeconds(10)))).Message);
        var state = fixture.Scope.VerifyReadOnly(); Assert.Equal(state.Journal.Revision, state.Latest.Revision);
        Assert.Equal("completed", state.Cancellation!.Status);
        var records = StepResultCodec.Read(state.Latest.Root, state.Total);
        if (cancellationWins) Assert.Empty(records);
        else Assert.Equal(BusinessOutcome.Succeeded, Assert.Single(records).Latest.Business);
        Assert.Single(state.Latest.Root.GetProperty("participants").EnumerateArray(), DispatchIntentCodec.IsParticipant);
        var before = source.Snapshot();
        if (cancellationWins) Assert.Throws<IOException>(() => terminal.RecordObservation(fixture.JournalId, proof));
        else terminal.RecordObservation(fixture.JournalId, proof);
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    [Fact(DisplayName = "A Synchronous Accepted Observer Cannot Reenter Publication Or Deadlock An Actual Native Proof")]
    public async Task RejectsReentrantObservers()
    {
        using var fixture = new StepWriterFixture("tail", judgement: new(AutomationJudgementLanguage.Keywords, ["ready"], [], null));
        using var observed = await ObserveRealOutput(fixture); var proof = observed.Proof; var source = fixture.Base.Source;
        var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
        var observer = new Observer(() =>
        {
            var before = new JournalStore(source.Instance, new()).Read(fixture.JournalId).Export();
            Assert.Throws<IOException>(() => fixture.Scope.RequireStart());
            Assert.Equal("execution_publication_reentrancy", Assert.Throws<IOException>(() => terminal.RecordObservation(fixture.JournalId, proof)).Message);
            Assert.Equal("execution_publication_reentrancy", Assert.Throws<IOException>(() => fixture.Scope.CloseAdmission(0)).Message);
            Assert.Equal(before, new JournalStore(source.Instance, new()).Read(fixture.JournalId).Export());
        });
        _ = await Task.Run(() => Cancel(fixture, observer)).WaitAsync(TimeSpan.FromSeconds(10));
        var state = fixture.Scope.VerifyReadOnly(); Assert.True(state.IsDispatchClosed);
        Assert.Empty(StepResultCodec.Read(state.Latest.Root, state.Total)); Assert.Equal(state.Journal.Revision, state.Latest.Revision);
    }

    [Fact(DisplayName = "Disposing The Lease Closes Publication Admission But Retains Its Actual Instance Lock Until Accepted Work Finishes")]
    public async Task DrainsDisposedLease()
    {
        using var source = new PlanBuilderFixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var pending = Task.Run(() => source.Lease.PublishExecution(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("fixture_publication_timeout");
            return 42;
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); source.Lease.Dispose();
            Assert.Throws<ObjectDisposedException>(() => source.Lease.PublishExecution(() => 0));
            Assert.Throws<IOException>(() => InstanceLease.Acquire(source.Instance, new()));
        }
        finally { release.Set(); }
        Assert.Equal(42, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await source.Lease.WhenExecutionPublicationsReleased.WaitAsync(TimeSpan.FromSeconds(10));
        using var next = InstanceLease.Acquire(source.Instance, new());
    }

    [Fact(DisplayName = "The Lease Publication Queue Rejects Its Sixty Fifth Pending Item Without Running It")]
    public async Task BoundsPendingPublication()
    {
        using var source = new PlanBuilderFixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var first = Task.Run(() => source.Lease.PublishExecution(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("fixture_publication_timeout");
            return -1;
        }));
        var queued = new List<Task<int>>();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            for (var index = 0; index < 63; index++)
            {
                var captured = index;
                queued.Add(Task.Factory.StartNew(() => source.Lease.PublishExecution(() => captured), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default));
            }
            await WaitCount(source.Lease, 64); var ran = false;
            Assert.Equal("execution_publication_capacity_exceeded", Assert.Throws<IOException>(() => source.Lease.PublishExecution(() => ran = true)).Message);
            Assert.False(ran);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(queued.Append(first)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(-1, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Enumerable.Range(0, 63), (await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(10))).Order());
    }

    private static async Task<RealObservation> ObserveRealOutput(StepWriterFixture fixture)
    {
        var source = fixture.Base.Source; var initial = fixture.Scope.VerifyReadOnly();
        var binding = StepResultCodec.Binding(initial.Latest.Root, Assert.Single(initial.Steps));
        var raw = new RawOutputRecorder(new(source.Instance, new()), binding);
        var capture = new ProcessOutputCapture(fixture.Output, raw);
        fixture.Scope.EnterApplying(); fixture.Writer.Start(fixture.Output);
        Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        Assert.True((await capture.DrainAsync(TimeSpan.FromSeconds(10))).IsComplete);
        _ = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        var artifact = await raw.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var factory = new StepEvidenceFactory(source.Instance, source.Database, new(), source.Lease);
        var evidence = fixture.Writer.UseStepEvidence(binding, null, (root, code) => factory.CreateLive(binding, artifact, false, root, code));
        Assert.Equal(BusinessOutcome.Succeeded, evidence.Business); Assert.Equal(EngineOutcome.Failed, evidence.Engine);
        return new(new(StepEvidenceAuthority.Live(factory, binding, evidence, fixture.Writer, null)), raw);
    }

    private static ExecutionCancellationResult Cancel(StepWriterFixture fixture, IExecutionCancellationObserver observer)
    {
        var source = fixture.Base.Source; var state = fixture.Scope.VerifyReadOnly();
        var identity = ExecutionCancellationStore.Identity(Guid.NewGuid(), "step-writer-fixture", fixture.JournalId, state.Journal.OperationId,
            Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
        return new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease, observer)
            .Cancel(identity, fixture.JournalId, PlanBuilderFixture.CreatedAt);
    }

    private static async Task WaitCount(InstanceLease lease, int expected)
    {
        var elapsed = Stopwatch.StartNew();
        while (lease.ExecutionPublicationCount != expected && elapsed.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(1);
        Assert.Equal(expected, lease.ExecutionPublicationCount);
    }

    private sealed class Observer(Action accepted) : IExecutionCancellationObserver
    {
        public void Reached(ExecutionCancellationPoint point) { if (point == ExecutionCancellationPoint.IntentAccepted) accepted(); }
    }

    private sealed record RealObservation(PreparedStepObservation Proof, RawOutputRecorder Raw) : IDisposable
    {
        public void Dispose() => Raw.Dispose();
    }
}
