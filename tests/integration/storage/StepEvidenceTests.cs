using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StepEvidenceTests
{
    [Fact(DisplayName = "A Real Keyword Success And Nonzero Root Exit Remain Different Facts With The Exact Raw Bytes")]
    public async Task SeparatesBusinessExit()
    {
        using var fixture = new StepWriterFixture("tail", judgement: new(AutomationJudgementLanguage.Keywords, ["ready"], [], null));
        var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start();
        Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        await collector.ObserveExitAsync();
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
        terminal.RecordCollected(fixture.JournalId, proof);
        var result = proof.Use(binding, value => value);
        Assert.Equal(EngineOutcome.Failed, result.Engine); Assert.Equal(BusinessOutcome.Succeeded, result.Business);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var document = StepEvidenceCodec.Read(blobs, binding, result.Artifact);
        Assert.Equal(37, document.ExitCode); Assert.NotNull(document.Closure);
        var raw = RawOutputCodec.Read(blobs, binding, document.RawOutput!);
        Assert.True(raw.IsComplete);
        Assert.Equal("ready\n中文尾行", Text(raw, blobs, NexusPipeline.Platform.Processes.ProcessOutputSource.StandardOutput));
        Assert.Equal("error\n错误尾行", Text(raw, blobs, NexusPipeline.Platform.Processes.ProcessOutputSource.StandardError));
        var reader = new StepEvidenceReader(source.Instance, source.Database, new());
        Assert.Equal(result, reader.Read(binding, result.Artifact, true));
        collector.Dispose();
        var references = reader.ReadReferences(binding, result.Artifact);
        Assert.All(raw.Parts, part => Assert.Contains(part.Content, references));
        using (var unavailable = new FileStream(Path.Combine(source.Instance.RuntimeRoot, raw.Parts[0].Content.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(references, reader.ReadReferences(binding, result.Artifact));
            Assert.Throws<IOException>(() => reader.Read(binding, result.Artifact, true));
        }
        var before = new JournalStore(source.Instance, new()).Read(fixture.JournalId).Export();
        Assert.Throws<IOException>(() => proof.Use(binding with { AttemptId = Guid.NewGuid() }, _ => true));
        Assert.Equal(before, new JournalStore(source.Instance, new()).Read(fixture.JournalId).Export());
    }

    [Fact(DisplayName = "A Cancelled Running Root Has Real Eof But Cannot Gain Business Success After Dispatch Closes")]
    public async Task RejectsCancelledSuccess()
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start(); await Ready(fixture);
        var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
        terminal.FreezeDispatch(fixture.JournalId, true);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10)); terminal.RecordCollected(fixture.JournalId, proof);
        var result = proof.Use(binding, value => value);
        Assert.Equal(EngineOutcome.Unknown, result.Engine); Assert.Equal(BusinessOutcome.Unverified, result.Business);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var document = StepEvidenceCodec.Read(blobs, binding, result.Artifact);
        Assert.Null(document.ExitCode); Assert.True(RawOutputCodec.Read(blobs, binding, document.RawOutput!).IsComplete);
    }

    [Fact(DisplayName = "An Unstarted Owned Output Closes Its Local Client Ends And Collects Zero Bytes Without Inventing A Root")]
    public async Task CollectsEmptyOutput()
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease).RecordCollected(fixture.JournalId, proof);
        var result = proof.Use(binding, value => value); Assert.Equal(EngineOutcome.NotStarted, result.Engine);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var document = StepEvidenceCodec.Read(blobs, binding, result.Artifact);
        Assert.Null(document.Root); Assert.Null(document.ExitCode);
        var raw = RawOutputCodec.Read(blobs, binding, document.RawOutput!);
        Assert.True(raw.IsComplete); Assert.Empty(raw.Parts); Assert.Equal(0, raw.ObservedBytes);
    }

    [Theory(DisplayName = "Recovery Collects Only From The Actual Stopped Family And Preserves Durable Facts Without Fabricating Eof")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesRecoveredFacts(bool recordObservation)
    {
        using var fixture = new StepWriterFixture("tail", judgement: new(AutomationJudgementLanguage.Keywords, [], [], null));
        var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start();
        Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        if (recordObservation) await collector.ObserveExitAsync();
        _ = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
        terminal.FreezeDispatch(fixture.JournalId, true);
        var proof = new StepEvidenceFactory(source.Instance, source.Database, new(), source.Lease).CreateInterruptedCollected(binding);
        terminal.RecordCollected(fixture.JournalId, proof);
        var result = proof.Use(binding, value => value);
        Assert.Equal(recordObservation ? EngineOutcome.Failed : EngineOutcome.Unknown, result.Engine);
        Assert.Equal(recordObservation ? BusinessOutcome.NotApplicable : BusinessOutcome.Unverified, result.Business);
        var document = StepEvidenceCodec.Read(new(source.Instance, new()), binding, result.Artifact);
        Assert.Null(document.RawOutput); Assert.Equal(recordObservation, document.PreviousObservation is not null);
        _ = await collector.CollectAsync(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(10));
    }

    [Fact(DisplayName = "A Generic Zero Exit Without Its Configured Completion Marker Retains Retryable Failure")]
    public async Task RejectsMissingCompletion()
    {
        using var fixture = new StepWriterFixture("quiet", judgement: new(AutomationJudgementLanguage.Keywords, ["done"], [], null));
        var source = fixture.Base.Source; var binding = Binding(fixture);
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start();
        Assert.Equal(0, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        await collector.ObserveExitAsync();
        var proof = await collector.CollectAsync(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(10));
        var result = proof.Use(binding, value => value);
        Assert.Equal(EngineOutcome.Succeeded, result.Engine); Assert.Equal(BusinessOutcome.Failed, result.Business);
        Assert.Equal("execution_completion_missing", result.ReasonCode);
        var document = StepEvidenceCodec.Read(new(source.Instance, new()), binding, result.Artifact);
        Assert.Equal(0, document.ExitCode); Assert.True(document.RawOutput is not null);
        Assert.Equal(result, new StepEvidenceReader(source.Instance, source.Database, new()).Read(binding, result.Artifact, true));
        new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease).FreezeDispatch(fixture.JournalId, true);
        Assert.Throws<IOException>(() => new StepEvidenceReader(source.Instance, source.Database, new()).Read(binding, result.Artifact, true));
    }

    private static StepEvidenceBinding Binding(StepWriterFixture fixture)
    {
        var state = fixture.Scope.VerifyReadOnly();
        return StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
    }

    private static async Task Ready(StepWriterFixture fixture)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(fixture.Marker + ".output-ready") && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(File.Exists(fixture.Marker + ".output-ready"));
    }

    private static string Text(RawOutputManifest raw, ImmutableBlobStore blobs, NexusPipeline.Platform.Processes.ProcessOutputSource source)
    {
        using var bytes = new MemoryStream();
        foreach (var part in raw.Parts.Where(part => part.Source == source)) { using var input = blobs.OpenVerified(part.Content); input.CopyTo(bytes); }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
