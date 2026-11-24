using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
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
        const int transcriptBudget = 2_097_152;
        const int progressSaturationFloor = 2_064_380;
        const int eventBudget = 10_002;
        Assert.InRange(control.Archive.SizeBytes, progressSaturationFloor, transcriptBudget);
        Assert.InRange(control.EventCount, 100, eventBudget);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var native = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var raw = RawOutputCodec.Read(blobs, result.Binding, native.RawOutput!);
        Assert.True(raw.IsComplete);
        var expected = Encoding.UTF8.GetBytes("provider-native-stdout" + Environment.NewLine);
        Assert.Equal(expected.LongLength, raw.ObservedBytes);
        using var output = new MemoryStream();
        foreach (var part in raw.Parts)
        {
            Assert.Equal(output.Length, part.Offset);
            using var bytes = blobs.OpenVerified(part.Content);
            bytes.CopyTo(output);
        }
        Assert.Equal(expected, output.ToArray());
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }
}
