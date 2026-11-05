using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LauncherPendingTests
{
    [Theory(DisplayName = "Actual Owner Kill At The Launcher Handoff Gap Uses The Original Held Job Before Restoring Configuration")]
    [InlineData("RetentionPrepared", "recoveredStopped")]
    [InlineData("HandoffApplied", "recoveredRetained")]
    public async Task RecoversHeldHandoff(
        string boundary, string expectedStage)
    {
        await using var fixture = await ProviderCrashFixture.StartAsync(
            collected: true, launcher: true, boundary: boundary, holdLauncherJob: true);
        await fixture.KillOwnerAsync();
        if (boundary == "RetentionPrepared") await fixture.StopLauncherAsync();
        else Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(fixture.LauncherRoot!));
        var instance = new InstanceRoot(new()).OpenExisting(fixture.Source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(fixture.Source.Root, "restart-diagnostics"),
            new JournalStartupAdmission(instance, new()));
        var recovery = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(fixture.Source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        new RecoveryCompletionService(instance, database, new(), lease, resources, index).ReconcilePending();
        var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase);
        var launcher = Assert.Single(ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        Assert.Equal(expectedStage, launcher.Stage); Assert.Equal(fixture.Launcher!.Binding, launcher.Binding);
        var evidence = new ProviderLauncherEvidence(instance, database, new());
        var recovered = evidence.Read<ProviderLauncherRecovered>(launcher.Artifact!);
        Assert.NotNull(recovered.Intent);
        Assert.Equal(expectedStage == "recoveredRetained" ? "retained" : "empty", recovered.Observation.Kind);
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.Equal(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        var registry = new ExecutionProviderRegistry();
        try
        {
            registry.RestoreSavedReferences(instance, database, new(), lease, resources);
            Assert.Equal(expectedStage == "recoveredRetained" ? 1 : 0, registry.RetainedCount);
            using var mutation = resources.TryAcquire(Guid.NewGuid(), [new("extension:provider-fixture", ResourceClaimMode.Mutation)]).Lease;
            Assert.Equal(expectedStage == "recoveredStopped", mutation is not null);
        }
        finally { await registry.StopMonitorAsync(); }
        _ = recovery.Recover(lease);
        Assert.Equal(receipt, new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(launcher.Stage, Assert.Single(ProviderLauncherCodec.Read(
            AttemptWriterState.Read(instance, database, new(), fixture.Parent).Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Stage);
    }

    [Fact(DisplayName = "Actual Handoff Without Its Original Job Or Recorded Retained Proof Keeps The Installed Configuration Quarantined")]
    public async Task IsolatesUnprovenHandoff()
    {
        await using var fixture = await ProviderCrashFixture.StartAsync(
            collected: true, launcher: true, boundary: "HandoffApplied");
        await fixture.KillOwnerAsync();
        await RequireMissingAsync(fixture.Launcher!.Binding.JobName);
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(fixture.LauncherRoot!));
        var installed = File.ReadAllBytes(fixture.Source.Configuration);
        var identity = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
        var marker = File.ReadAllBytes(fixture.Fixture.Marker);
        var instance = new InstanceRoot(new()).OpenExisting(fixture.Source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(fixture.Source.Root, "restart-diagnostics"),
            new JournalStartupAdmission(instance, new()));
        var recovery = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(fixture.Source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease);
        var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        Assert.Equal(JournalPhase.Quarantined, state.Journal.Phase);
        Assert.Equal("retentionPrepared", Assert.Single(ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Stage);
        Assert.Null(new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.True(index.IsBlocked(state.Journal.ResourceKeys.ToHashSet(StringComparer.Ordinal), out _));
        Assert.Contains(index.Current.Isolations, item => item.JournalId == fixture.Parent);
        Assert.Equal(installed, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        Assert.NotEqual(fixture.OriginalIdentity, identity);
        _ = recovery.Recover(lease);
        Assert.Null(new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(installed, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.Equal(marker, File.ReadAllBytes(fixture.Fixture.Marker));
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(fixture.LauncherRoot!));
    }

    private static async Task RequireMissingAsync(string name)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            using (var job = JobNative.OpenJobObjectW(4, false, name))
                if (job.IsInvalid) { Assert.Equal(2, Marshal.GetLastPInvokeError()); return; }
            await Task.Delay(20);
        }
        Assert.Fail("The original Native Job name did not disappear after owner exit");
    }
}
