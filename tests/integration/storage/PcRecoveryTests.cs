using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PcRecoveryTests
{
    [Fact(DisplayName = "Owner Exit After Sql Target Root Admission Without A Necessary Closure Keeps The Installed Configuration Quarantined")]
    public async Task IsolatesUnclosedTargets()
    {
        using var fixture = new PcCrashFixture();
        await fixture.StartAsync("RootAdmitted", holdEmptyNecessaryJobs: true);
        Assert.False(File.Exists(fixture.Fixture.GameMarker));
        await fixture.KillOwnerAsync(); await fixture.RequireStoppedAsync();
        var source = fixture.Source;
        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        Assert.Equal(JournalPhase.Quarantined, state.Journal.Phase); Assert.Null(new AttemptReceiptStore(database).Read(fixture.Attempt));
        RequireIsolated(index, state);
        Assert.NotEqual(fixture.OriginalBytes, File.ReadAllBytes(source.Configuration));
        Assert.NotEqual(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(source.Configuration));
        using var installed = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(source.Configuration));
        Assert.True(installed.RootElement.GetProperty("fixture").GetBoolean());
        Assert.Empty(PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        Assert.All(state.Jobs, job => Assert.Null(new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(fixture.Parent, job.WriterId, job.JobName, job.SessionId)));
    }

    [Fact(DisplayName = "Owner Exit After Actual Retention Intent Stops The Game And Recovers Its Original Complete Family")]
    public async Task RecoversRetainedFamily()
    {
        using var fixture = new PcCrashFixture();
        await fixture.StartAsync("RetentionPrepared");
        Assert.True(File.Exists(fixture.Fixture.GameMarker));
        await fixture.KillOwnerAsync(); await fixture.RequireStoppedAsync();
        var state = Recover(fixture, "recoveredStopped", BusinessOutcome.Succeeded);
        var target = Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        var recovery = new TargetJobEvidence(fixture.Source.Instance, fixture.Source.Database, new())
            .Read<TargetRecoveryDocument>(target.Evidence!);
        Assert.NotNull(recovery.Intent);
        Assert.Empty(recovery.Observation.LivingFamily);
    }

    [Theory(DisplayName = "Owner Exit After Real Lifetime Handoff Preserves The Living Game And Restores From Authenticated Ownership")]
    [InlineData("HandoffApplied", "recoveredRetained", true)]
    [InlineData("retained", "retained", false)]
    public async Task PreservesTransferredLifetime(
        string point, string expectedStage, bool holdJob)
    {
        using var fixture = new PcCrashFixture();
        await fixture.StartAsync(point, holdOriginalJob: holdJob);
        await fixture.KillOwnerAsync(); Assert.True(fixture.GameAlive);
        _ = Recover(fixture, expectedStage, BusinessOutcome.Succeeded);
        Assert.True(fixture.GameAlive);
    }

    [Fact(DisplayName = "A Physically Retained Game Without Its Original Job Or Recorded Handoff Cannot Be Assumed Stopped Or Owned After Owner Exit")]
    public async Task RejectsUnrecordedOwnership()
    {
        using var fixture = new PcCrashFixture();
        await fixture.StartAsync("HandoffApplied");
        await fixture.KillOwnerAsync(); Assert.True(fixture.GameAlive);
        using (var missing = NexusPipeline.Platform.Processes.JobNative.OpenJobObjectW(4, false, fixture.Binding.JobName))
        {
            Assert.True(missing.IsInvalid); Assert.Equal(2, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        }
        var source = fixture.Source;
        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        Assert.Equal(JournalPhase.Quarantined, state.Journal.Phase);
        Assert.Equal("retentionPrepared", Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Stage);
        Assert.Null(new AttemptReceiptStore(database).Read(fixture.Attempt)); RequireIsolated(index, state);
        Assert.True(fixture.GameAlive);
        Assert.NotEqual(fixture.OriginalBytes, File.ReadAllBytes(source.Configuration));
        Assert.NotEqual(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(source.Configuration));
    }

    [Fact(DisplayName = "A Flushed Target Stage Ahead Of Sql Is Replayed But Cannot Invent An Unrecorded Main Closure")]
    public async Task ReplaysSavedStage()
    {
        using var fixture = new PcCrashFixture();
        await fixture.StartAsync("stage-ahead", holdEmptyNecessaryJobs: true);
        Assert.Equal(fixture.Report.GetProperty("sqlRevision").GetInt64() + 1, fixture.Report.GetProperty("physicalRevision").GetInt64());
        await fixture.KillOwnerAsync(); await fixture.RequireStoppedAsync();
        var source = fixture.Source;
        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        Assert.Equal(JournalPhase.Quarantined, state.Journal.Phase); Assert.Equal(state.Latest.Revision, state.Journal.Revision);
        Assert.Equal(TargetExecutionOutcome.OwnedReady,
            Assert.Single(PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Outcome);
        Assert.Empty(StepResultCodec.Read(state.Latest.Root, state.Total));
        Assert.Null(new AttemptReceiptStore(database).Read(fixture.Attempt)); RequireIsolated(index, state);
        Assert.NotEqual(fixture.OriginalBytes, File.ReadAllBytes(source.Configuration));
    }

    private static void RequireIsolated(RecoveryIndex index, AttemptWriterState state)
    {
        Assert.True(index.Current.IsReady);
        Assert.True(index.IsBlocked(state.Journal.ResourceKeys.ToHashSet(StringComparer.Ordinal), out _));
        Assert.Contains(index.Current.Isolations, isolation => isolation.JournalId == state.Journal.JournalId);
    }

    private static AttemptWriterState Recover(PcCrashFixture fixture, string targetStage, BusinessOutcome business)
    {
        var source = fixture.Source;
        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        new RecoveryCompletionService(instance, database, new(), lease, resources, index).ReconcilePending();
        var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase); Assert.Equal(state.Journal.Revision, state.Latest.Revision);
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(business, receipt.Facts.Business);
        Assert.Equal(targetStage, Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Stage);
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(source.Configuration));
        Assert.Equal(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.True(index.Current.IsReady);
        Assert.DoesNotContain(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations, isolation => isolation.JournalId == fixture.Parent);
        return state;
    }
}
