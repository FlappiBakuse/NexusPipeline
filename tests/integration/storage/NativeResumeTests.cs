using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NativeResumeTests
{
    [Theory(DisplayName = "Original Packaged Native Continuation Retains Selection And Latest Progress After Host Closes The Failed Process Family")]
    [InlineData("okww", "okww:daily")]
    [InlineData("zzzonedragon", "zzzonedragon:01/coffee")]
    public async Task ContinuesOriginalEntry(string extension, string target)
    {
        await using var fixture = new NativeResumeFixture(extension);
        await fixture.InstallAsync();
        var accepted = fixture.Service.Start(Guid.NewGuid(), "native-resume",
            fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)), DateTimeOffset.UtcNow);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Configuration));
        var attempts = File.ReadAllLines(fixture.Log + ".attempts");
        Assert.Equal(2, attempts.Length); Assert.Equal(attempts[0], attempts[1]);
        Assert.Equal(new[] { "0", "1" }, File.ReadAllLines(fixture.Log + ".progress"));
        Assert.True(File.Exists(fixture.Log + ".restart-verified"));
        Assert.Equal("{\"ordinal\":0,\"remaining\":4}", File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture.Configuration)!, "progress.json")));
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(new[] { target }, invocation.TaskRetry!.Selection.Targets);
        var step = Assert.Single(state.Total.Steps);
        Assert.Equal(step.Tasks.Where(task => task.IsSelected).Select(task => task.TaskId), invocation.TaskRetry.Selection.SelectedIds);
        if (extension == "zzzonedragon") Assert.Contains("zzzonedragon:01/notify", invocation.TaskRetry.Selection.SelectedIds);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, step, evidence.TaskAdapter!);
        Assert.All(observed.Observations, item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
        if (extension == "zzzonedragon") Assert.Equal(AdapterCompletion.Satisfied,
            observed.Observations.Single(item => item.TaskId == "zzzonedragon:01/email").CompletionKind);
        ConfigurationRunInventory.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
        fixture.Inputs.VerifyUnchanged();
    }
}
