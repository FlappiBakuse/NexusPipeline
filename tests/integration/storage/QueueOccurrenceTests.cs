using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
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
        Guid[] bindings = [source.BindingB, source.BindingC, source.BindingA, source.OtherBinding, source.BindingB, source.BindingC, source.BindingA];
        Assert.Equal(bindings.Select(id => id.ToString("D")), plan.Steps.Select(step => step.BindingId));
        Assert.Equal(plan.Steps.Where(step => step.ScriptId == source.ScriptId.ToString("D")).Take(3).Select(step => step.StepId),
            plan.Steps[3].DependsOn);
        Assert.All(plan.Steps.Where((_, index) => index != 3), step => Assert.Empty(step.DependsOn));
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
            var report = Path.Combine(source.Source.Root, "repeat-root");
            var changes = new AutomationChanges(catalog.Revision, [], [script with
            {
                Arguments = configuration
                    ? ["process-output", "configuration-repeat", Path.Combine(source.Source.Root, "repeated-writes"), source.Source.Configuration, report]
                    : [.. script.Arguments, report]
            }], [],
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
        var jobs = state.AllJobs.Where(job => job.Role == "process").ToDictionary(job => job.StepId);
        Assert.Equal(2, jobs.Values.Select(job => job.JobName).Distinct().Count());
        var roots = Directory.GetFiles(fixture.Source.Root, "repeat-root-*").Select(OwnedWorkerRoot.Read)
            .OrderBy(root => root.CreatedAtUtc).ThenBy(root => root.ProcessId).ToArray();
        Assert.Equal(2, roots.Length);
        Assert.NotEqual(roots[0], roots[1]);
        var results = StepResultCodec.Read(state.Latest.Root, state.Total).ToDictionary(result => result.Binding.StepId);
        Assert.Equal(2, results.Count);
        var closures = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new());
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        for (var index = 0; index < state.Total.Steps.Count; index++)
        {
            var step = state.Total.Steps[index];
            var root = roots[index].RequireStopped(closures, fixture.Accepted.JournalId, jobs[step.StepId]);
            var result = results[step.StepId];
            Assert.True(result.IsCollected);
            var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
            Assert.Equal(root, evidence.Root);
            Assert.Equal(configuration ? 0 : 37, evidence.ExitCode);
            var output = RawOutputCodec.Read(blobs, result.Binding, Assert.IsType<BlobReference>(evidence.RawOutput));
            Assert.True(output.IsComplete);
            Assert.Equal(configuration ? "done\n"u8.ToArray() : "ready\n中文尾行"u8.ToArray(),
                CapturedStream(blobs, output, ProcessOutputSource.StandardOutput));
            Assert.Equal(configuration ? Array.Empty<byte>() : "error\n错误尾行"u8.ToArray(),
                CapturedStream(blobs, output, ProcessOutputSource.StandardError));
        }
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
            var script = catalog.Scripts.Single(script => script.Id == first.ScriptId);
            var report = Path.Combine(source.Source.Root, "cancel-root");
            var changes = new AutomationChanges(catalog.Revision, [], [script with { Arguments = [.. script.Arguments, report] }], [],
                [.. catalog.Bindings.Where(binding => binding.Id != first.Id).Select(binding => binding with { IsEnabled = false })],
                [queue with { ScriptIds = [first.ScriptId, first.ScriptId] }], [], []);
            source.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "repeated-cancel", changes), changes, DateTimeOffset.UtcNow);
        });
        var original = File.ReadAllBytes(fixture.Source.Configuration);
        var running = fixture.Consumer.RunNextAsync();
        OwnedWorkerRoot observed;
        AttemptReceipt receipt;
        try
        {
            await fixture.Ready(".output-ready");
            var pid = int.Parse(File.ReadAllText(fixture.Marker + ".output-ready"), System.Globalization.CultureInfo.InvariantCulture);
            var root = ProcessIdentityReader.Read(pid);
            Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(root));
            observed = OwnedWorkerRoot.From(root);
        }
        finally
        {
            fixture.Cancel();
            receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        var state = fixture.State;
        Assert.Equal(2, state.Total.Steps.Count);
        var first = state.Total.Steps[0]; var second = state.Total.Steps[1];
        Assert.Equal(first.BindingId, second.BindingId);
        Assert.NotEqual(first.StepId, second.StepId);
        Assert.Equal(observed, OwnedWorkerRoot.Read(Assert.Single(Directory.GetFiles(fixture.Source.Root, "cancel-root-*"))));
        var job = Assert.Single(state.AllJobs.Where(job => job.Role == "process"));
        Assert.Equal(first.StepId, job.StepId);
        _ = observed.RequireStopped(new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new()),
            fixture.Accepted.JournalId, job);
        Assert.DoesNotContain(StepResultCodec.Read(state.Latest.Root, state.Total), result => result.Binding.StepId == second.StepId);
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    private static byte[] CapturedStream(ImmutableBlobStore blobs, RawOutputManifest output, ProcessOutputSource source)
    {
        using var bytes = new MemoryStream();
        foreach (var part in output.Parts.Where(part => part.FileSourceId is null && part.Source == source))
        {
            using var content = blobs.OpenVerified(part.Content);
            content.CopyTo(bytes);
        }
        return bytes.ToArray();
    }
}
