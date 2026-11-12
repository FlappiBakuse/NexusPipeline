using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderLauncherTests
{
    [Theory(DisplayName = "The Declared Launcher Lives In A Separate Domain Only After The Actual Worker Terminal And Complete Stop")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainsSeparateLauncher(bool configuration)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync(
            configuration ? "configuration-launcher" : "terminal-success", configuration, launcher: true);
        var original = File.ReadAllBytes(fixture.Source.Configuration);
        var originalIdentity = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
        var receipt = await fixture.RunAsync();
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        var state = fixture.State;
        var launcher = Assert.Single(ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        Assert.Equal("retained", launcher.Stage);
        var evidence = new ProviderLauncherEvidence(fixture.Source.Instance, fixture.Source.Database, new());
        var retained = evidence.Read<ProviderLauncherRetained>(launcher.Artifact!);
        var intent = evidence.Read<ProviderLauncherRetention>(retained.Intent);
        var worker = RequireWorkerStopped(fixture);
        Assert.NotEqual(worker.JobName, launcher.Binding.JobName);
        Assert.DoesNotContain(state.Jobs, job => job.JobName == launcher.Binding.JobName);
        Assert.True(retained.Native.Inventory.ActiveProcesses > 0);
        Assert.Equal(0U, retained.Native.EffectiveLimitFlags);
        Assert.All(retained.Native.Inventory.LivingFamily, member =>
        {
            Assert.Equal(fixture.Package.Launcher, member.ImagePath, ignoreCase: true);
            Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(member));
        });
        Assert.Equal(Assert.Single(ProviderTerminalCodec.Read(state.Latest.Root, state.Total)).Artifact, intent.Terminal);
        Assert.NotEmpty(intent.NecessaryClosures);
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        var conflict = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("extension:provider-fixture", ResourceClaimMode.Mutation)]);
        Assert.Null(conflict.Lease); Assert.Equal(ResourceLeaseDenial.Conflict, conflict.Denial);
        using var script = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("automation:" + fixture.Source.OtherScriptId.ToString("D"),
            ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(script);
        if (configuration)
        {
            await UntilAsync(() => File.Exists(fixture.LauncherMarker));
            using var marker = JsonDocument.Parse(File.ReadAllBytes(fixture.LauncherMarker));
            Assert.Equal("{\"fixture\":true}", marker.RootElement.GetProperty("observedConfiguration").GetString());
            Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
            Assert.Equal(originalIdentity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
            Assert.Equal(2, new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new()).Read(fixture.Address!)!.Revision);
        }
        var capacity = JournalCapacityPolicy.Measure(state.Total);
        var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId);
        Assert.True(history.Count <= capacity.Revisions); Assert.True(history.Sum(item => item.Export().LongLength) <= capacity.HistoryBytes);
        Assert.All(history, item => Assert.True(item.Export().LongLength <= capacity.MaximumDocumentBytes));
    }

    [Fact(DisplayName = "The Original Held Job Publishes An Immutable Ending Leaf Before Its Package References Are Released")]
    public async Task PublishesEndingLeaf()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("terminal-success", launcher: true);
        _ = await fixture.RunAsync();
        var before = fixture.State;
        var launcher = Assert.Single(ProviderLauncherCodec.Read(before.Latest.Root, before.Total, before.Attempt.ExecutionPlan.Sha256));
        File.WriteAllText(fixture.LauncherRelease, "release");
        await UntilAsync(() => ProviderLauncherRows.Ended(fixture.Source.Database, launcher.Binding, launcher.Artifact!) is not null);
        await UntilAsync(() => fixture.Registry.RetainedCount == 0);
        var leaf = ProviderLauncherRows.Ended(fixture.Source.Database, launcher.Binding, launcher.Artifact!)!;
        var evidence = new ProviderLauncherEvidence(fixture.Source.Instance, fixture.Source.Database, new());
        var ended = evidence.Read<ProviderLauncherEnded>(leaf);
        Assert.Equal(launcher.Artifact, ended.Retained); Assert.Equal(launcher.Binding, ended.Binding);
        Assert.Equal(0U, ended.Inventory.ActiveProcesses); Assert.Empty(ended.Inventory.LivingFamily);
        Assert.Equal(ended.Inventory.AllFamily.Length, ended.Inventory.RetainedKernelObjects);
        Assert.Equal(ended.Inventory.AllFamily.Length, ended.Inventory.AllFamily.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count());
        Assert.All(ended.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.Equal(before.Latest.Revision, fixture.State.Latest.Revision); Assert.Equal(JournalPhase.Committed, fixture.State.Journal.Phase);
        using var update = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("extension:provider-fixture", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(update);
        Assert.Contains(leaf, evidence.RequireAll(fixture.State, verifyContents: false));
    }

    [Fact(DisplayName = "No Durable Worker Terminal Forces A Complete Launcher Stop Without Inventing A Provider Success")]
    public async Task StopsUnprovenLauncher()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("no-terminal", launcher: true);
        var receipt = await fixture.RunAsync();
        Assert.Equal(EngineOutcome.Unknown, receipt.Facts.Engine);
        Assert.Empty(ProviderTerminalCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        RequireLauncherStopped(fixture); _ = RequireWorkerStopped(fixture);
        Assert.DoesNotContain(ProviderLauncherCodec.ReadAll(fixture.State.Latest.Root, fixture.State.Total, fixture.State.Attempt.ExecutionPlan.Sha256),
            item => item.Stage is "retentionPrepared" or "retained");
    }

    [Fact(DisplayName = "An Accepted Cancel Before The First Terminal Stops Both Actual Domains Without Adding Business Success")]
    public async Task CancelsBothDomains()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("wait", launcher: true);
        var running = fixture.RunAsync(); await fixture.ReadyAsync(); fixture.Cancel();
        var receipt = await running;
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Empty(ProviderTerminalCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        RequireLauncherStopped(fixture); _ = RequireWorkerStopped(fixture);
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }

    [Fact(DisplayName = "A Real Native Launcher Creation Failure Preserves The Unstarted Main And The Actual Failure Evidence")]
    public async Task PreservesCreationFailure()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("terminal-success", launcher: true, invalidLauncher: true);
        _ = await Assert.ThrowsAnyAsync<Exception>(() => fixture.RunAsync());
        var state = fixture.State;
        var main = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(EngineOutcome.NotStarted, main.Latest.Engine); Assert.Equal(BusinessOutcome.Unverified, main.Latest.Business);
        Assert.Empty(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
        var stopped = RequireLauncherStopped(fixture);
        Assert.False(stopped.StartReturned); Assert.True(stopped.NativeStartErrorCode > 0);
        Assert.Equal(EngineOutcome.Failed, Assert.Single(ProviderLauncherCodec.ReadAll(state.Latest.Root, state.Total,
            state.Attempt.ExecutionPlan.Sha256), item => item.Stage == "stopped").LaunchEngine);
        var receipt = new AttemptReceiptStore(fixture.Source.Database).Read(Guid.Parse(state.Attempt.AttemptId));
        Assert.NotNull(receipt); Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
    }

    [Fact(DisplayName = "An Unknown Living Launcher Child Is Fully Stopped Rather Than Removed From The Inventory To Permit Retention")]
    public async Task StopsUnknownChildren()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("terminal-after-launcher", launcher: true, launcherChild: true);
        _ = await fixture.RunAsync();
        var stopped = RequireLauncherStopped(fixture);
        Assert.Contains(stopped.Inventory.AllFamily, member => member.ImagePath.Contains("other-game", StringComparison.Ordinal));
        Assert.DoesNotContain(ProviderLauncherCodec.ReadAll(fixture.State.Latest.Root, fixture.State.Total,
            fixture.State.Attempt.ExecutionPlan.Sha256), item => item.Stage is "retentionPrepared" or "retained");
        _ = RequireWorkerStopped(fixture);
    }

    [Fact(DisplayName = "Retained And Ended History Uses Saved References After The Deployed Package And Activation Disappear")]
    public async Task ReadsSavedRetention()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("terminal-success", launcher: true);
        _ = await fixture.RunAsync();
        File.WriteAllText(fixture.LauncherRelease, "release");
        await UntilAsync(() => fixture.Registry.RetainedCount == 0);
        var state = fixture.State;
        var evidence = new ProviderLauncherEvidence(fixture.Source.Instance, fixture.Source.Database, new());
        var expected = evidence.RequireAll(state, verifyContents: true);
        var launcher = Assert.Single(ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        using (var historical = new FileStream(Path.Combine(fixture.Source.Instance.RuntimeRoot, launcher.Artifact!.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var restored = new NexusPipeline.Platform.Extensions.ExecutionProviderRegistry();
            restored.RestoreSavedReferences(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Base.Resources);
            Assert.Equal(0, restored.RetainedCount);
        }
        await fixture.Scope.DisposeAsync();
        File.Delete(fixture.Package.Worker); File.Delete(fixture.Package.Launcher!);
        Assert.Equal(expected, evidence.RequireAll(fixture.State, verifyContents: true));
        var closure = new PlanReferenceClosure(fixture.Source.Instance, new(), new(fixture.Source.Instance, new()), fixture.Source.Database)
            .Read(state.Journal, fixture.Accepted.Plan);
        Assert.All(expected, reference => Assert.Contains(reference, closure));
    }

    private static FrozenJobClosure RequireWorkerStopped(ProviderRuntimeFixture fixture)
    {
        var state = fixture.State; var main = Assert.Single(state.Steps);
        var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(state.Journal.JournalId, main.WriterId, main.JobName, main.SessionId);
        Assert.NotNull(closure); Assert.True(closure.WasExecuted); Assert.NotNull(closure.Root);
        Assert.Contains(closure.Root, closure.Family);
        Assert.Equal(closure.Family.Length, closure.Family.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count());
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        return closure;
    }

    private static ProviderLauncherStopped RequireLauncherStopped(ProviderRuntimeFixture fixture)
    {
        var state = fixture.State;
        var launcher = Assert.Single(ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        Assert.Equal("stopped", launcher.Stage);
        var stopped = new ProviderLauncherEvidence(fixture.Source.Instance, fixture.Source.Database, new())
            .Read<ProviderLauncherStopped>(launcher.Artifact!);
        Assert.Equal(0U, stopped.Inventory.ActiveProcesses); Assert.Empty(stopped.Inventory.LivingFamily);
        Assert.Equal(stopped.Inventory.AllFamily.Length, stopped.Inventory.RetainedKernelObjects);
        Assert.All(stopped.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        return stopped;
    }

    private static async Task UntilAsync(Func<bool> complete)
    {
        var elapsed = Stopwatch.StartNew();
        while (!complete() && elapsed.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
        Assert.True(complete());
    }
}
