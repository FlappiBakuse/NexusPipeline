using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BetterGiRetryTests
{
    [Fact(DisplayName = "Original BetterGI Package Restarts After Host Closes Failed Process Family With Only Failed Task Selected And Original Configuration Restored")]
    public async Task RetriesFailedTask()
    {
        await using var fixture = new BetterGiRetryFixture();
        await fixture.InstallAsync();
        var accepted = fixture.Service.Start(Guid.NewGuid(), "bettergi-restart",
            fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)), DateTimeOffset.UtcNow);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Configuration));
        var attempts = File.ReadAllLines(fixture.Log + ".attempts");
        Assert.Equal(2, attempts.Length);
        Assert.Equal(new[] { "mail", "resin" }, JsonSerializer.Deserialize<string[]>(attempts[0]));
        Assert.Equal(new[] { "resin" }, JsonSerializer.Deserialize<string[]>(attempts[1]));
        Assert.True(File.Exists(fixture.Log + ".restart-verified"));
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(new[] { "bettergi:resin" }, invocation.TaskRetry!.Selection.Targets);
        Assert.Equal(new[] { "bettergi:resin" }, invocation.TaskRetry.Selection.SelectedIds);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, Assert.Single(state.Total.Steps), evidence.TaskAdapter!);
        Assert.All(observed.Observations.Where(item => item.TaskId is "bettergi:mail" or "bettergi:resin"),
            item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
        ConfigurationRunInventory.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
        fixture.Inputs.VerifyUnchanged();
    }
}
