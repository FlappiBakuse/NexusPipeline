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
public sealed class BaahTests
{
    [Theory(DisplayName = "Original BAAH Package Restarts Required Login After Host Kills The Failed Script And Child Family Without Replaying Successful Business")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartsLogin(bool automatic)
    {
        await using var fixture = new BaahFixture(automatic);
        await fixture.InstallAsync();
        var accepted = fixture.Service.Start(Guid.NewGuid(), "baah-restart",
            fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)), DateTimeOffset.UtcNow);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Configuration));
        var attempts = File.ReadAllLines(fixture.Log + ".attempts");
        Assert.Equal(2, attempts.Length);
        using var retry = JsonDocument.Parse(attempts[1]);
        Assert.Equal(automatic, retry.RootElement.GetProperty("automatic").GetBoolean());
        Assert.True(retry.RootElement.GetProperty("cleanup").GetBoolean());
        Assert.Equal(new[] { true, false, true }, retry.RootElement.GetProperty("flags").EnumerateArray().Select(item => item.GetBoolean()));
        Assert.True(File.Exists(fixture.Log + ".restart-verified"));
        Assert.Equal(new[] { "0", "1" }, File.ReadAllLines(fixture.Log + ".software"));
        Assert.Equal("{\"SAVE_LOG_TO_FILE\":true,\"fixture_progress\":0}",
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture.Configuration)!, "software.json")));
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(new[] { "baah:0/2/邮件" }, invocation.TaskRetry!.Selection.Targets);
        Assert.Contains("baah:0/0/登录游戏", invocation.TaskRetry.Selection.SelectedIds);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var states = invocation.ConfigurationPlans.Select(reference =>
            new NexusPipeline.Platform.Storage.Journals.ConfigurationPlanCodec(fixture.Source.Instance, new(), blobs).ReadStructure(reference))
            .Select(plan => new RunStateCodec(fixture.Source.Instance, new(), blobs).ReadStructure(plan.RunState!))
            .ToDictionary(item => item.Source.Address);
        var auxiliary = Assert.Single(states.Values, item => item.Source.Address.ResourceId == "software-config");
        Assert.Empty(auxiliary.Tasks); Assert.NotNull(auxiliary.InputReference);
        PreparedRetryOrigin.RequireSaved(fixture.Source.Instance, fixture.Source.Database, new(), state.Attempt, state.Total, result.Binding.StepId, states);
        states[auxiliary.Source.Address] = auxiliary with { InputReference = null, InputTree = null };
        Assert.Throws<IOException>(() => PreparedRetryOrigin.RequireSaved(fixture.Source.Instance, fixture.Source.Database, new(),
            state.Attempt, state.Total, result.Binding.StepId, states));
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, Assert.Single(state.Total.Steps), evidence.TaskAdapter!);
        Assert.All(observed.Observations.Where(item => item.TaskId is "baah:0/1/咖啡馆" or "baah:0/2/邮件"),
            item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
        ConfigurationRunInventory.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
        fixture.Inputs.VerifyUnchanged();
    }
}
