using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class QueueOccurrenceTests
{
    [Fact(DisplayName = "Repeated Script Selections Freeze Distinct Ordered Steps With Their Original Binding Identities")]
    public void FreezesRepeatedSelections()
    {
        using var source = new PlanBuilderFixture();
        var catalog = source.Definitions.Read(); var queue = catalog.Queues.Single();
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [],
            [queue with { ScriptIds = [source.ScriptId, source.OtherScriptId, source.ScriptId],
                SuccessDependencies = [new(source.OtherScriptId, [source.ScriptId])] }], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "repeated-fixture", changes), changes, DateTimeOffset.UtcNow);
        using var draft = source.Prepare(new QueueTarget(source.QueueId));
        var plan = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        Assert.Equal(7, plan.Steps.Count);
        Assert.Equal(plan.Steps.Count, plan.Steps.Select(step => step.StepId).Distinct().Count());
        Assert.Equal(6, plan.Steps.Count(step => step.ScriptId == source.ScriptId.ToString("D")));
        Assert.Equal(source.OtherScriptId.ToString("D"), plan.Steps[3].ScriptId);
        Assert.Equal(plan.Steps.Take(3).Select(step => step.BindingId), plan.Steps.Skip(4).Select(step => step.BindingId));
        Assert.Equal(plan.Steps.Take(3).Select(step => step.StepId), plan.Steps[3].DependsOn);
    }

    [Theory(DisplayName = "Repeated Binding Occurrences Create Distinct Real Workers And Only Their Last Occurrence Publishes Configuration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutesRepeatedBindings(bool configuration)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: configuration, configure: source =>
        {
            var catalog = source.Source.Definitions.Read(); var queue = catalog.Queues.Single();
            var first = catalog.Bindings.First(binding => binding.IsEnabled && binding.ScriptId == queue.ScriptIds[0]);
            var script = catalog.Scripts.Single(script => script.Id == first.ScriptId);
            var changes = new AutomationChanges(catalog.Revision, [], configuration ? [script with {
                Arguments = ["process-output", "configuration-repeat", Path.Combine(source.Source.Root, "repeated-writes"), source.Source.Configuration]
            }] : [], [],
                [.. catalog.Bindings.Where(binding => binding.Id != first.Id).Select(binding => binding with { IsEnabled = false })],
                [queue with { ScriptIds = [first.ScriptId, first.ScriptId] }], [], []);
            source.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "repeated-fixture", changes), changes, DateTimeOffset.UtcNow);
        });
        var original = File.ReadAllBytes(fixture.Source.Configuration);
        Assert.NotNull(await fixture.Consumer.RunNextAsync().WaitAsync(TimeSpan.FromSeconds(45)));
        var state = fixture.State;
        Assert.Equal(2, state.Total.Steps.Count); Assert.Equal(2, state.Steps.Length);
        Assert.Equal(state.Total.Steps[0].BindingId, state.Total.Steps[1].BindingId);
        Assert.NotEqual(state.Total.Steps[0].StepId, state.Total.Steps[1].StepId);
        Assert.Equal(2, state.Jobs.Where(job => job.Role == "process").Select(job => job.JobName).Distinct().Count());
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
        if (configuration)
        {
            Assert.False(state.Total.Steps[0].UpdateConfigurationAtFinish);
            Assert.True(state.Total.Steps[1].UpdateConfigurationAtFinish);
            var resource = Assert.Single(new ValueService(fixture.Source.Instance, fixture.Source.Database, new(),
                fixture.Source.Lease, fixture.Base.Resources).Read(Guid.Parse(state.Total.Steps[0].BindingId!)));
            Assert.Equal(2, resource.Revision);
            var saved = new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new()).Read(
                new(Guid.Parse(state.Total.Steps[0].BindingId!), resource.ResourceId))!;
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("{\"fixture\":false,\"occurrence\":2}"u8)),
                Assert.Single(saved.Tree.Entries).Sha256);
        }
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelsRepeatedBindings(bool configuration)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: configuration, mode: "wait", configure: source =>
        {
            var catalog = source.Source.Definitions.Read(); var queue = catalog.Queues.Single();
            var first = catalog.Bindings.First(binding => binding.IsEnabled && binding.ScriptId == queue.ScriptIds[0]);
            var changes = new AutomationChanges(catalog.Revision, [], [], [],
                [.. catalog.Bindings.Where(binding => binding.Id != first.Id).Select(binding => binding with { IsEnabled = false })],
                [queue with { ScriptIds = [first.ScriptId, first.ScriptId] }], [], []);
            source.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "repeated-cancel", changes), changes, DateTimeOffset.UtcNow);
        });
        var original = File.ReadAllBytes(fixture.Source.Configuration);
        var running = fixture.Consumer.RunNextAsync();
        await fixture.Ready(".output-ready"); fixture.Cancel();
        var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(2, fixture.State.Total.Steps.Count);
        Assert.Single(fixture.State.Jobs.Where(job => job.Role == "process"));
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.True(fixture.Base.Index.Current.IsReady);
    }
}
