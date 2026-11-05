using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PcStageTests
{
    [Fact(DisplayName = "A Real Gui Launcher Can Exit While Only Its Verified Living Game Receives The Retained Lifetime")]
    public async Task RetainsVerifiedGame()
    {
        var fixture = new PcStageFixture(forceClose: false, hooks: false, targetMode: "launcher");
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var state = fixture.State;
            var target = Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal("retained", target.Stage);
            var root = Assert.IsType<ProcessIdentity>(TargetRootFacts.Read(fixture.Source.Database, target.Binding));
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(root));
            Assert.NotEqual(fixture.GameImage, root.ImagePath);
            var handoff = new TargetJobEvidence(fixture.Source.Instance, fixture.Source.Database, new())
                .Read<TargetRetainedDocument>(target.Evidence!);
            Assert.True(handoff.Native.Inventory.AllFamily.Length >= 2);
            var game = Assert.Single(handoff.Native.Inventory.LivingFamily);
            Assert.Equal(fixture.GameImage, game.ImagePath); Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(game));
            await fixture.AwaitGameAsync(); Assert.True(fixture.GameIsAlive);
            Assert.DoesNotContain(state.Jobs, job => job.JobName == target.Binding.JobName);
        });
    }

    [Fact(DisplayName = "A Game That Actually Exits During Main Has A Real Stopped Receipt Even When The Policy Would Retain A Living Game")]
    public async Task RecordsExitedGame()
    {
        var fixture = new PcStageFixture(forceClose: false, mainWait: true, hooks: false, gameCanExit: true);
        await fixture.ExecuteAsync(async () =>
        {
            var running = fixture.Consumer.RunNextAsync();
            await fixture.AwaitMainAsync(); await fixture.ExitGameAsync(); fixture.Cancel();
            var receipt = Assert.IsType<AttemptReceipt>(await running);
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var state = fixture.State;
            var target = Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal("stopped", target.Stage);
            var proof = new TargetJobEvidence(fixture.Source.Instance, fixture.Source.Database, new())
                .Read<TargetStoppedDocument>(target.Evidence!);
            Assert.Equal(0U, proof.Inventory.ActiveProcesses); Assert.NotEmpty(proof.Inventory.AllFamily);
            Assert.All(proof.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
        });
    }

    [Fact(DisplayName = "Without Hooks The Actual Game Reads Installed Saved Configuration Before Main And The Original File Returns Afterward")]
    public async Task InstallsBeforeMain()
    {
        var fixture = new PcStageFixture(configuration: true, hooks: false, distinctConfiguration: true);
        await fixture.ExecuteAsync(async () =>
        {
            var original = File.ReadAllBytes(fixture.Source.Configuration);
            var identity = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            using var observed = JsonDocument.Parse(File.ReadAllBytes(fixture.GameMarker));
            using var installed = JsonDocument.Parse(observed.RootElement.GetProperty("observedConfiguration").GetString()!);
            Assert.True(installed.RootElement.GetProperty("fixture").GetBoolean());
            Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
            Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
            Assert.Single(fixture.State.Jobs);
            Assert.False(File.Exists(fixture.HookMarker + ".pre")); Assert.False(File.Exists(fixture.HookMarker + ".post"));
        });
    }

    [Theory(DisplayName = "Pre Then Owned Game Then Main Uses Independent Jobs And Restores Configuration After Post")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparatesTargetJobs(bool configuration)
    {
        var fixture = new PcStageFixture(configuration);
        await fixture.ExecuteAsync(async () =>
        {
            var original = File.ReadAllBytes(fixture.Source.Configuration);
            var identity = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var state = fixture.State;
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
            var stage = Assert.Single(PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal(TargetExecutionOutcome.OwnedReady, stage.Outcome);
            var target = Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal("stopped", target.Stage);
            var stopped = new TargetJobEvidence(fixture.Source.Instance, fixture.Source.Database, new())
                .Read<TargetStoppedDocument>(target.Evidence!);
            Assert.Equal(0U, stopped.Inventory.ActiveProcesses);
            Assert.All(stopped.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            Assert.DoesNotContain(state.Jobs, writer => writer.JobName == target.Binding.JobName);
            var main = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            Assert.Equal(EngineOutcome.Succeeded, main.Latest.Engine); Assert.Equal(BusinessOutcome.Succeeded, main.Latest.Business);
            Assert.NotEmpty(new PcStageEvidence(fixture.Source.Instance, fixture.Source.Database, new())
                .RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.True(File.Exists(fixture.HookMarker + ".pre")); Assert.True(File.Exists(fixture.HookMarker + ".post"));
            Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration)); Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
            Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
            var bound = JournalCapacityPolicy.Measure(state.Total);
            var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(fixture.Accepted.JournalId);
            Assert.True(history.Count <= bound.Revisions); Assert.True(history.Sum(item => item.Export().LongLength) <= bound.HistoryBytes);
        });
    }

    [Theory(DisplayName = "A Real Target Stage Failure Never Becomes A Main Failure Or Starts Post")]
    [InlineData("invalid", TargetExecutionOutcome.LaunchFailed)]
    [InlineData("timeout", TargetExecutionOutcome.ReadinessTimedOut)]
    public async Task PreservesStageFailure(string mode, TargetExecutionOutcome expected)
    {
        var fixture = new PcStageFixture(targetMode: mode);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var state = fixture.State;
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
            var primary = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            Assert.Equal(EngineOutcome.NotStarted, primary.Latest.Engine); Assert.Equal(BusinessOutcome.Unverified, primary.Latest.Business);
            Assert.Null(new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new()).Context(primary.Binding).Root);
            Assert.Equal(expected, Assert.Single(PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Outcome);
            Assert.Equal("stopped", Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Stage);
            Assert.False(File.Exists(fixture.HookMarker + ".main")); Assert.False(File.Exists(fixture.HookMarker + ".post"));
            Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
        });
    }

    [Fact(DisplayName = "A Preexisting Ready Game Is Observed Without Receiving A Kill Authority Even With Force Close")]
    public async Task PreservesPreexistingOwnership()
    {
        var fixture = new PcStageFixture(external: true);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(TargetExecutionOutcome.ExternalReady,
                Assert.Single(PcStageCodec.Read(fixture.State.Latest.Root, fixture.State.Total, fixture.State.Attempt.ExecutionPlan.Sha256)).Outcome);
            Assert.Empty(TargetJobCodec.Read(fixture.State.Latest.Root, fixture.State.Total, fixture.State.Attempt.ExecutionPlan.Sha256));
            Assert.True(fixture.GameIsAlive);
        });
    }

    [Theory(DisplayName = "Launch Disabled Runs Main Without Allocating Or Starting A Target Job")]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task SkipsDisabledLaunch(bool configuration, bool hooks)
    {
        var fixture = new PcStageFixture(configuration: configuration, launch: false, hooks: hooks);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(TargetExecutionOutcome.Skipped,
                Assert.Single(PcStageCodec.Read(fixture.State.Latest.Root, fixture.State.Total, fixture.State.Attempt.ExecutionPlan.Sha256)).Outcome);
            Assert.Empty(TargetJobCodec.Read(fixture.State.Latest.Root, fixture.State.Total, fixture.State.Attempt.ExecutionPlan.Sha256));
            Assert.False(File.Exists(fixture.GameMarker)); Assert.True(File.Exists(fixture.HookMarker + ".main"));
        });
    }

    [Theory(DisplayName = "Actual Cancellation Closes All Necessary Writers Before The Frozen Game Cleanup Decision")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosesNecessaryWriters(bool forceClose)
    {
        var fixture = new PcStageFixture(forceClose: forceClose, mainWait: true);
        await fixture.ExecuteAsync(async () =>
        {
            var running = fixture.Consumer.RunNextAsync();
            await fixture.AwaitMainAsync(); fixture.Cancel();
            var receipt = Assert.IsType<AttemptReceipt>(await running);
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var state = fixture.State;
            Assert.All(state.Jobs, writer =>
            {
                Assert.NotNull(writer.ClosedElapsedMilliseconds);
                var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
                    .ReadForExecutionEvidence(state.Journal.JournalId, writer.WriterId, writer.JobName, writer.SessionId);
                Assert.NotNull(closure);
                Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            });
            Assert.Equal(forceClose ? "stopped" : "retained",
                Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Stage);
            Assert.Equal(!forceClose, fixture.GameIsAlive);
            Assert.False(File.Exists(fixture.HookMarker + ".post")); Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
        });
    }
}
