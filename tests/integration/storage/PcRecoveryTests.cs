using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PcRecoveryTests(ITestOutputHelper output)
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
        if (holdJob) VerifyRetainedObservation(fixture);
        _ = Recover(fixture, expectedStage, BusinessOutcome.Succeeded);
        Assert.True(fixture.GameAlive);
    }

    private void VerifyRetainedObservation(PcCrashFixture fixture)
    {
        var source = fixture.Source;
        var binding = fixture.Binding;
        var state = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.Parent);
        var image = state.Total.Steps.Single(step => step.StepId == binding.StepId).Target!.Pc!.ReadyImage;
        var frozen = new PcTargetImage(image.Path, image.SizeBytes, image.Sha256, new(image.Identity.VolumeSerial, image.Identity.FileId));
        var creator = TargetRootFacts.Creator(source.Database, binding);
        var root = TargetRootFacts.Read(source.Database, binding);
        var writer = Console.Error;
        using var text = new StringWriter();
        int members;
        try
        {
            Console.SetError(text);
            using var profile = StartupProfile.Begin("pc-retained-image");
            var changed = frozen with { SizeBytes = frozen.SizeBytes + 1 };
            Assert.Equal("execution_target_recovery_native_unverified", Assert.Throws<IOException>(() =>
                PreparedRecoveryObservation.Capture(binding.JobName, binding.SessionId, creator, root, changed).Dispose()).Message);
            using var observation = PreparedRecoveryObservation.Capture(binding.JobName, binding.SessionId, creator, root, frozen);
            members = observation.Use(binding.JobName, binding.SessionId, creator, root, actual =>
            {
                Assert.Equal("retained", actual.Kind);
                Assert.NotEmpty(actual.LivingFamily);
                Assert.All(actual.LivingFamily, member => Assert.Equal(frozen.Sha256, member.ImageSha256));
                return actual.LivingFamily.Length;
            });
        }
        finally { Console.SetError(writer); }
        using var report = JsonDocument.Parse(text.ToString());
        var hashes = report.RootElement.GetProperty("steps").EnumerateArray()
            .Single(step => step.GetProperty("name").GetString() == "process.image-hash").GetProperty("count").GetInt64();
        output.WriteLine($"live retained family: one rejected size, {members} process identities, {hashes} protocol image hash observations");
        Assert.Equal(1, hashes);
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
