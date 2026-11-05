using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ControlBudgetTests
{
    [Fact(DisplayName = "Actual Progress Saturation Preserves The Reserved Terminal And Keeps Control Bytes Outside Raw Output")]
    public async Task ReservesTerminalCapacity()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("progress-limit");
        var receipt = await fixture.RunAsync();
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        Assert.Equal("execution_provider_transcript_limit", File.ReadAllText(fixture.Marker + ".budget-hit"));
        var state = fixture.State;
        var terminal = Assert.Single(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
        var control = new ProviderControlReader(fixture.Source.Instance, fixture.Source.Database, new())
            .Read(terminal.Binding, terminal.Artifact);
        Assert.Equal(terminal.Decision, control.Terminal);
        Assert.Null(control.Archive.FaultCode);
        Assert.InRange(control.Archive.SizeBytes,
            ExecutionProviderFrames.MaximumTranscriptBytes - ExecutionProviderFrames.TerminalReserveBytes - ExecutionProviderFrames.MaximumEventBytes,
            ExecutionProviderFrames.MaximumTranscriptBytes);
        Assert.InRange(control.EventCount, 100, ProviderProtocolState.MaximumEvents);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var native = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), result.Binding, result.Latest.Artifact);
        var raw = RawOutputCodec.Read(new(fixture.Source.Instance, new()), result.Binding, native.RawOutput!);
        Assert.True(raw.IsComplete);
        Assert.InRange(raw.ObservedBytes, 1, 1024);
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }
}
