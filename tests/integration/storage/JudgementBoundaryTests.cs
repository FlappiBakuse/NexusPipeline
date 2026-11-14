using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementBoundaryTests
{
    private const string FinalOnly = "function nexusJudge(value) { return {outcome:value.isFinal?'succeeded':'unverified',reason:'actual final'}; }";

    [Fact(DisplayName = "A Missing Required Judgement Job Cannot Enter Effects And The Actual Empty Primary Can Be Cancelled Without Creating The Worker")]
    public async Task RequiresRegisteredWorker()
    {
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript, FinalOnly);
        await using var step = new JudgementOwnedStep(fixture);
        step.Writer!.RegisterPreparedJob(); var before = fixture.Source.Snapshot();
        Assert.Throws<IOException>(step.Scope.EnterApplying);
        var after = fixture.Source.Snapshot(); Assert.Equal(before.Files, after.Files); Assert.Equal(before.Rows, after.Rows);
        Assert.Single(fixture.State.Jobs); Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        await step.CompletePreparedCancellation();
        var state = fixture.State; Assert.Equal(JournalPhase.Committed, state.Journal.Phase); Assert.Equal(EffectGateState.Sealed, state.Gate.State);
        var job = Assert.Single(state.Jobs); Assert.Equal("process", job.Role);
        AssertEmptyClosure(fixture, job); Assert.Equal(1, fixture.Base.Count("journal_writer_jobs"));
    }

    [Fact(DisplayName = "A Failed Judgement Job Sql Registration Retains Both Actual Native Owners And Replays Their Empty Closures Without Resuming")]
    public async Task RetainsFailedRegistration()
    {
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript, FinalOnly);
        await using var step = new JudgementOwnedStep(fixture);
        fixture.Base.Execute($"CREATE TRIGGER main.fail_judgement_registration BEFORE INSERT ON journal_writer_jobs " +
            $"WHEN NEW.journal_id='{fixture.Accepted.JournalId:D}' AND EXISTS(SELECT 1 FROM main.journal_writer_jobs WHERE journal_id=NEW.journal_id) " +
            "BEGIN SELECT RAISE(ABORT,'actual_worker_registration_failure'); END;");
        Assert.Throws<SqliteException>(step.Prepare);
        Assert.NotNull(step.Worker); Assert.Equal(1, fixture.Base.Count("journal_writer_jobs"));
        Assert.Equal(2, fixture.State.Jobs.Length); Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        fixture.Base.Execute("DROP TRIGGER main.fail_judgement_registration;");
        await step.CompletePreparedCancellation();
        var state = fixture.State; Assert.Equal(JournalPhase.Committed, state.Journal.Phase);
        Assert.Equal(2, fixture.Base.Count("journal_writer_jobs")); Assert.Equal(2, fixture.Base.Count("journal_writer_job_closures"));
        Assert.All(state.Jobs, job => AssertEmptyClosure(fixture, job));
        Assert.Empty(StepResultCodec.Read(state.Latest.Root, state.Total));
    }

    [Fact(DisplayName = "A Stopped Configuration Primary Cannot Seal Or Restore Its Scene Until The Separate Actual Judgement Family Has Stopped")]
    public async Task RequiresStoppedJudgement()
    {
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript, FinalOnly, "wait", configuration: true);
        await using var step = new JudgementOwnedStep(fixture);
        step.Prepare(); step.Start(); await fixture.Ready(); var stopped = await step.StopPrimary();
        await step.Collector.DrainOutputAsync(TimeSpan.FromSeconds(15));
        await step.Worker!.WhenIdle.WaitAsync(TimeSpan.FromSeconds(15));
        var source = fixture.Source; var session = step.Controller!.Read(); var resource = Assert.Single(session.Resources);
        var before = source.Snapshot();
        Assert.Throws<IOException>(() => new EditOutputRecorder(source.Instance, source.Database, new(), source.Lease).Seal(resource.JournalId, stopped));
        var recovery = new ConfigurationRecoverySession(source.Instance, resource.JournalId, new(), source.Database,
            () => new(source.Instance.InstanceId, resource.JournalId, true, WriterState.Stopped));
        Assert.Throws<IOException>(recovery.BeginRestoration);
        var after = source.Snapshot(); Assert.Equal(before.Files, after.Files); Assert.Equal(before.Rows, after.Rows);
        var worker = Assert.Single(fixture.State.Jobs, job => job.Role == "judgement");
        Assert.Null(worker.ClosedElapsedMilliseconds); Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveJob(worker.JobName, worker.SessionId));
        await step.Collect(); await step.Complete();
        Assert.Equal(ConfigurationRunPhase.Completed, step.Controller.Read().Phase);
        Assert.Equal(JournalPhase.Committed, fixture.State.Journal.Phase);
        Assert.All(fixture.State.Jobs, job => AssertStoppedClosure(fixture, job));
    }

    [Fact(DisplayName = "An Accepted Cancellation Without A Physical Close Rejects The Actual Worker Write Gate Before Its Frame Delegate Runs")]
    public async Task RejectsCancelledEntry()
    {
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript, FinalOnly, "wait");
        await using var step = new JudgementOwnedStep(fixture);
        step.Writer!.RegisterPreparedJob(); var source = fixture.Source;
        var scope = new StepWriterScope(source.Instance, source.Database, new(), source.Lease,
            fixture.Accepted.JournalId, step.Scope.StepId, "judgement", Guid.NewGuid());
        using var worker = new ConfigurationWriterProcess(source.Instance, source.Lease, scope);
        using var output = new OwnedProcessOutput(redirectInput: true);
        using var primaryOutput = new OwnedProcessOutput();
        try
        {
            step.Scope.EnterApplying(); worker.Start(output); step.Writer.Start(primaryOutput); await fixture.Ready();
            var state = fixture.State;
            var cancellation = ExecutionCancellationStore.Identity(Guid.NewGuid(), JudgementIntegrationFixture.Subject,
                state.Journal.JournalId, state.Journal.OperationId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
            var store = new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease,
                new CancellationFailure(ExecutionCancellationPoint.IntentAccepted));
            Assert.Throws<IOException>(() => store.Cancel(cancellation, state.Journal.JournalId, DateTimeOffset.UtcNow));
            Assert.Null(DispatchIntentCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
            var before = source.Snapshot(); var writes = 0;
            Assert.Throws<IOException>(() => worker.AdmitJudgementWrite(_ => { writes++; return Task.CompletedTask; },
                CancellationToken.None, output, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
            Assert.Equal(0, writes); var after = source.Snapshot(); Assert.Equal(before.Files, after.Files); Assert.Equal(before.Rows, after.Rows);
        }
        finally { await worker.StopAsync(TimeSpan.FromSeconds(10)); await step.StopPrimary(); }
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_closures"));
        Assert.All(fixture.State.Jobs, job => AssertStoppedClosure(fixture, job));
    }

    [Fact(DisplayName = "Real Escaped Log Inputs Exhaust The Periodic Byte Allowance Without Consuming The Reserved Final Generation")]
    public async Task PreservesFinalAllowance()
    {
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript, FinalOnly, "judgement-capacity");
        await using var step = new JudgementOwnedStep(fixture);
        step.Prepare(); step.Start(); await fixture.Ready();
        const long evidenceLimit = 64L * 1024 * 1024;
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new()); ExecutionJudgementEvidence? exhausted = null;
        var total = fixture.Source.Lease.PublishExecution(() => fixture.State.Total);
        var inputBuilder = new JudgementInputBuilder(fixture.Source.Instance, fixture.Source.Database, new());
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            step.Collector.TryRequestStop(); await Task.Delay(100);
            if (step.Owner.IsBusy || step.Worker!.Snapshot() is not { } snapshot) continue;
            var pending = JudgementEvidenceCodec.Read(blobs, step.Binding, snapshot);
            if (pending.Exchanges.IsEmpty) continue;
            var raw = await step.Collector.SnapshotOutputAsync();
            var next = inputBuilder.Prepare(total, pending.Identity, raw).MaximumEncodedBytes;
            if (next > JudgementEvidenceBudget.MaximumInputBytes(evidenceLimit, EvidenceBytes(pending), final: false))
            { exhausted = pending; break; }
        }
        Assert.NotNull(exhausted);
        var prior = exhausted.Exchanges;
        Assert.All(prior, item => Assert.False(JudgementInvocationArchive.Read(blobs, item.Invocation).IsFinal));
        await step.Collect(); await step.Complete();
        var state = fixture.State; var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var final = JudgementEvidenceCodec.Read(blobs, result.Binding, evidence.Judgement!);
        Assert.Equal(prior.Length + 1, final.Exchanges.Length);
        Assert.Equal(prior.Select(item => item.Invocation), final.Exchanges.Take(prior.Length).Select(item => item.Invocation));
        var last = JudgementInvocationArchive.Decode(blobs, final.Exchanges[^1].Invocation);
        Assert.True(last.IsFinal); Assert.Equal(prior.Length + 1L, last.Generation);
        Assert.Equal(BusinessOutcome.Succeeded, evidence.Business);
        Assert.True(EvidenceBytes(final) <= evidenceLimit);

        long EvidenceBytes(ExecutionJudgementEvidence transcript) => transcript.Exchanges.Sum(item =>
            JudgementInvocationArchive.Read(blobs, item.Invocation).SizeBytes + (item.Response?.SizeBytes ?? 0) +
            item.ProbeObservations.Sum(probe => JudgementProbeArchive.Read(blobs, probe).SizeBytes));
    }

    private static void AssertEmptyClosure(JudgementIntegrationFixture fixture, StepWriterState job)
    {
        var value = AssertStoppedClosure(fixture, job);
        Assert.False(value.WasExecuted); Assert.Null(value.Root); Assert.Empty(value.Family); Assert.Equal(0L, value.RunElapsedMilliseconds);
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
    }

    private static FrozenJobClosure AssertStoppedClosure(JudgementIntegrationFixture fixture, StepWriterState job)
    {
        var closure = Assert.IsType<FrozenJobClosure>(new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(fixture.Accepted.JournalId, job.WriterId, job.JobName, job.SessionId));
        Assert.Equal(job.ClosedElapsedMilliseconds, closure.RunElapsedMilliseconds);
        Assert.All(closure.Family, process => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(process)));
        return closure;
    }
}
