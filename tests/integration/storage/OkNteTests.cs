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
public sealed class OkNteTests
{
    [Fact(DisplayName = "Original OkNTE Package Restarts After Host Closes Failed Process Family With Only Failed Routine Selected And Original Configuration Restored")]
    public async Task RetriesFailedRoutine()
    {
        await using var fixture = new OkNteFixture();
        await fixture.InstallAsync();
        var accepted = fixture.Service.Start(Guid.NewGuid(), "oknte-restart",
            fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)), DateTimeOffset.UtcNow);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Configuration));
        var attempts = File.ReadAllLines(fixture.Log + ".attempts");
        Assert.Equal(2, attempts.Length);
        Assert.Equal(new[] { "coffee", "daily_claim" }, JsonSerializer.Deserialize<string[]>(attempts[0]));
        Assert.Equal(new[] { "daily_claim" }, JsonSerializer.Deserialize<string[]>(attempts[1]));
        Assert.True(File.Exists(fixture.Log + ".restart-verified"));
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
        var retry = Assert.IsType<ExecutionTaskRetry>(invocation.TaskRetry);
        Assert.Equal(new[] { "oknte:daily_claim" }, retry.Selection.Targets);
        Assert.Equal(new[] { "oknte:daily_claim" }, retry.Selection.SelectedIds);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var step = Assert.Single(state.Total.Steps);
        var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, step, evidence.TaskAdapter!);
        Assert.NotEqual(retry.PreviousBinding.JobName, result.Binding.JobName);
        var first = AdapterObservationCodec.Read(blobs, retry.PreviousBinding, retry.PreviousRaw, step, retry.PreviousObservation);
        var second = observed.CurrentObservations.GetValueOrDefault();
        Assert.False(second.IsDefault);
        string[] ids = ["oknte:coffee", "oknte:daily_claim"];
        Assert.Equal(ids, first.Observations.Select(item => item.TaskId).Order(StringComparer.Ordinal));
        Assert.Equal(ids, second.Select(item => item.TaskId).Order(StringComparer.Ordinal));
        Assert.Equal(AdapterObservationState.Succeeded, Assert.Single(first.Observations, item => item.TaskId == "oknte:coffee").State);
        Assert.Equal(AdapterObservationState.Failed, Assert.Single(first.Observations, item => item.TaskId == "oknte:daily_claim").State);
        Assert.Equal(AdapterObservationState.Succeeded, Assert.Single(second, item => item.TaskId == "oknte:daily_claim").State);
        Assert.Equal(AdapterObservationState.Unknown, Assert.Single(second, item => item.TaskId == "oknte:coffee").State);
        Assert.Collection(observed.Observations
            .OrderBy(item => item.TaskId, StringComparer.Ordinal),
            coffee => { Assert.Equal("oknte:coffee", coffee.TaskId); Assert.Equal(AdapterObservationState.Succeeded, coffee.State); },
            daily => { Assert.Equal("oknte:daily_claim", daily.TaskId); Assert.Equal(AdapterObservationState.Succeeded, daily.State); });
        ConfigurationRunInventory.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
        fixture.Inputs.VerifyUnchanged();
    }
}
