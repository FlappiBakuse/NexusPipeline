using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BusinessCompletionTests
{
    [Theory(DisplayName = "Only Persisted Keyword Decisions Signal And Failure Stops While Success Keeps Its Grace")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesJudgementGrace(bool success)
    {
        using var fixture = new StepWriterFixture("wait", judgement: new(AutomationJudgementLanguage.Keywords,
            success ? ["ready"] : [], success ? [] : ["ready"], null));
        var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start();
        var decision = await collector.BusinessAccepted.WaitAsync(TimeSpan.FromSeconds(10));
        var expected = success ? BusinessOutcome.Succeeded : BusinessOutcome.Failed;
        var state = fixture.Scope.VerifyReadOnly();
        Assert.Equal(expected, Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total)).Latest.Business);
        var requested = collector.TryRequestStop();
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease).RecordCollected(fixture.JournalId, proof);
        var evidence = proof.Use(binding, value => value);
        Assert.Equal(expected, decision.Outcome); Assert.Equal(!success, requested); Assert.Equal(expected, evidence.Business);
        Assert.Equal(EngineOutcome.Unknown, evidence.Engine);
        var document = StepEvidenceCodec.Read(new(source.Instance, new()), binding, evidence.Artifact);
        Assert.Equal(success ? null : "execution_business_failed", document.Stop?.ReasonCode);
    }

    [Fact(DisplayName = "An Otherwise Silent Successful Root Receives The Actual Sixty Second Natural Exit Grace")]
    public async Task WaitsForGrace()
    {
        using var fixture = new StepWriterFixture("wait", judgement: new(AutomationJudgementLanguage.Keywords, ["ready"], [], null));
        var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start(); await collector.BusinessAccepted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(collector.TryRequestStop());
        await Task.Delay(TimeSpan.FromSeconds(60));
        var requested = collector.TryRequestStop();
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        var evidence = proof.Use(binding, value => value);
        Assert.True(requested); Assert.True(stopped.Closure.RunElapsedMilliseconds >= 60000);
        Assert.Equal(BusinessOutcome.Succeeded, evidence.Business); Assert.Equal(EngineOutcome.Unknown, evidence.Engine);
        var document = StepEvidenceCodec.Read(new(source.Instance, new()), binding, evidence.Artifact);
        Assert.Equal("execution_success_grace_expired", document.Stop!.ReasonCode);
        Assert.True(document.Stop.ElapsedMilliseconds - document.Stop.BusinessAcceptedElapsedMilliseconds >= 60000);
    }

    [Fact(DisplayName = "An Actually Executed None Mode Is Not Applicable Even When The Real Exit Code Is Nonzero")]
    public async Task PreservesUnjudgedExit()
    {
        using var fixture = new StepWriterFixture("tail", judgement: new(AutomationJudgementLanguage.Keywords, [], [], null));
        var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start(); Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        await collector.ObserveExitAsync();
        var proof = await collector.CollectAsync(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(10));
        new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease).RecordCollected(fixture.JournalId, proof);
        var evidence = proof.Use(binding, value => value);
        Assert.Equal(EngineOutcome.Failed, evidence.Engine); Assert.Equal(BusinessOutcome.NotApplicable, evidence.Business);
        Assert.False(collector.BusinessAccepted.IsCompleted);
        Assert.Equal(evidence, new StepEvidenceReader(source.Instance, source.Database, new()).Read(binding, evidence.Artifact, true));
    }

    [Theory(DisplayName = "None Mode Cannot Label An Unstarted Or Cancelled Writer Not Applicable")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsUnverifiedStops(bool unstarted)
    {
        using var fixture = new StepWriterFixture("wait", judgement: new(AutomationJudgementLanguage.Keywords, [], [], null));
        var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying();
        if (!unstarted)
        {
            collector.Start();
            await WaitReady(fixture.Marker + ".output-ready");
            var state = fixture.Scope.VerifyReadOnly();
            new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease).Cancel(
                ExecutionCancellationStore.Identity(Guid.NewGuid(), "step-writer-fixture", fixture.JournalId, state.Journal.OperationId,
                    Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), fixture.JournalId, DateTimeOffset.UtcNow);
        }
        var proof = await collector.CollectAsync(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(10));
        var evidence = proof.Use(binding, value => value);
        Assert.Equal(BusinessOutcome.Unverified, evidence.Business);
        Assert.Equal(unstarted ? EngineOutcome.NotStarted : EngineOutcome.Unknown, evidence.Engine);
    }

    [Fact(DisplayName = "Not Applicable Cannot Be Forged For A Configured Keyword Mode On A Read Only Path")]
    public async Task RejectsForgedJudgement()
    {
        using var fixture = new StepWriterFixture("tail"); var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start(); Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        var proof = await collector.CollectAsync(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(10));
        var artifact = proof.Use(binding, value => value.Artifact); var blobs = new ImmutableBlobStore(source.Instance, new());
        var document = StepEvidenceCodec.Read(blobs, binding, artifact);
        var forged = StepEvidenceCodec.Write(blobs, document with { Business = BusinessOutcome.NotApplicable, ReasonCode = "execution_judgement_not_applicable" });
        var before = source.Snapshot();
        Assert.Throws<IOException>(() => new StepEvidenceReader(source.Instance, source.Database, new()).Read(binding, forged, true));
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    private static StepEvidenceBinding Binding(StepWriterFixture fixture)
    {
        var state = fixture.Scope.VerifyReadOnly(); return StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
    }
    private static async Task WaitReady(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path)) await Task.Delay(10, timeout.Token);
    }
}
