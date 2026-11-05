using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class March7thTests
{
    [Theory(DisplayName = "Original March7th ZIP Runs Through Saved YAML, Actual Host Jobs, Failed Subset Retry And Exact Restoration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriesOriginalPackage(bool child)
    {
        await using var fixture = new March7thFixture(child);
        await fixture.InstallAsync();
        var selection = fixture.Selection();
        var discovery = await fixture.Adapters.DiscoverAsync(March7thFixture.Subject, selection, default);
        Assert.Equal(33, discovery.Tasks.Length);
        Assert.Contains(discovery.Tasks, task => task.TaskId == "march7th:daily_enable" && task.IsSelected && task.IsRetrySafe);
        var configured = await fixture.Adapters.ProposeConfigurationAsync(March7thFixture.Subject,
            selection with { OperationId = Guid.NewGuid() }, default);
        Assert.Empty(configured.Diagnostics.Where(item => item.Severity == AdapterDiagnosticSeverity.Blocking));
        var accepted = fixture.Service.Start(Guid.NewGuid(), March7thFixture.Subject,
            fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)), DateTimeOffset.UtcNow);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Configuration));
        var attempts = File.ReadAllLines(fixture.Log + ".attempts");
        Assert.Equal(2, attempts.Length);
        Assert.Equal(new[] { "universe_count: 7", "universe_count: 8" }, File.ReadAllLines(fixture.Log + ".progress"));
        Assert.Equal(child ? "reward_enable,reward_mail_enable" : "daily_enable", attempts[1]);
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, Assert.Single(state.Total.Steps), evidence.TaskAdapter!);
        Assert.All(observed.Observations.Where(item => item.TaskId == "march7th:daily_enable" ||
            item.TaskId == (child ? "march7th:reward_mail_enable" : "march7th:forgottenhall_enable")),
            item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
        var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
        var inputPlan = Assert.Single(invocation.ConfigurationPlans.Select(reference =>
            new NexusPipeline.Platform.Storage.Journals.ConfigurationPlanCodec(fixture.Source.Instance, new(), blobs).ReadStructure(reference)));
        var input = new RunStateCodec(fixture.Source.Instance, new(), blobs).ReadStructure(inputPlan.RunState!);
        Assert.NotNull(input.InputReference);
        PreparedRetryOrigin.RequireSaved(fixture.Source.Instance, fixture.Source.Database, new(), state.Attempt, state.Total,
            result.Binding.StepId, new Dictionary<NexusPipeline.Core.Configuration.ConfigurationAddress, FrozenRunState> { [input.Source.Address] = input });
        Assert.Throws<IOException>(() => PreparedRetryOrigin.RequireSaved(fixture.Source.Instance, fixture.Source.Database, new(),
            state.Attempt, state.Total, result.Binding.StepId,
            new Dictionary<NexusPipeline.Core.Configuration.ConfigurationAddress, FrozenRunState>
                { [input.Source.Address] = input with { InputReference = null, InputTree = null } }));
        ConfigurationRunInventory.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
        _ = new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new()).Read(result.Binding, result.Latest.Artifact, true);
        fixture.Inputs.VerifyUnchanged();
    }
}
