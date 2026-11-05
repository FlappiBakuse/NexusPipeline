using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidCancellationTests
{
    [Fact(DisplayName = "Cancellation Under The Production Five Second Stop Budget Collects Actual Android Commands And Confirms Device Shutdown")]
    public async Task CancelsRunningTarget()
    {
        var fixture = new AndroidRuntimeFixture(mainWait: true, stopSeconds: 5);
        await fixture.ExecuteAsync(async () =>
        {
            var running = fixture.Consumer.RunNextAsync();
            await fixture.MainReadyAsync(); fixture.Cancel();
            var receipt = Assert.IsType<AttemptReceipt>(await running);
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var collected = fixture.Collected();
            Assert.Equal(TargetCleanupOutcome.Completed, collected.Cleanup.Outcome);
            Assert.Equal(TargetCleanupVerification.DeviceOffline, collected.Cleanup.Verification);
            Assert.Equal(2, collected.Cleanup.ConfirmationCommands.Length);
            Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
            Assert.Empty(fixture.Consumer.ReadActive(AndroidRuntimeFixture.Subject));
        });
    }

    [Fact(DisplayName = "A Real Command Root Publication Failure Stops Its Suspended Family Without Inventing A No Effect Or Complete Response")]
    public async Task StopsFailedPublication()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false);
        await fixture.ExecuteAsync(async () =>
        {
            fixture.Base.Execute("CREATE TRIGGER reject_android_root BEFORE INSERT ON execution_target_job_roots " +
                "WHEN NEW.command_sequence IS NOT NULL BEGIN SELECT RAISE(ABORT,'fixture_android_root_failure'); END;");
            Exception? failure;
            try { failure = await Record.ExceptionAsync(async () => await fixture.Consumer.RunNextAsync()); }
            finally { fixture.Base.Execute("DROP TRIGGER reject_android_root;"); }
            Assert.NotNull(failure);
            Assert.Contains("fixture_android_root_failure", failure.ToString());
            Assert.Equal(1, fixture.Consumer.PendingReconciliationCount);
            var state = fixture.State;
            var command = Assert.Single(AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Null(TargetRootFacts.ReadCommand(fixture.Source.Database, command));
            var saved = Assert.Single(fixture.Collected().Commands);
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var collected = reader.Collection(state, command, saved.Collection, true);
            var observed = reader.Observation(state, command, collected.Observation, true);
            Assert.Null(observed.Root);
            Assert.Equal(EngineOutcome.Unknown, observed.Engine);
            Assert.Equal(AndroidResponseCompletion.Unverified, observed.Completion);
            Assert.True(collected.Inventory.TotalProcesses > 0);
            Assert.All(collected.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            Assert.Throws<IOException>(() => AndroidCollectedCodec.RequireResolved(state.Latest.Root, state.Total,
                state.Attempt.ExecutionPlan.Sha256));
            Assert.False(Directory.Exists(Path.Combine(fixture.Directory, "observed")));
            Assert.False(File.Exists(fixture.Marker + ".main"));
        });
    }

    [Fact(DisplayName = "An Accepted Cancellation Before Consumption Creates No Target Command Or Main And Preserves Original Configuration")]
    public async Task CancelsBeforeDispatch()
    {
        var fixture = new AndroidRuntimeFixture(configuration: true);
        await fixture.ExecuteAsync(async () =>
        {
            fixture.Cancel();
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
            Assert.Empty(AndroidControlCodec.Read(fixture.State.Latest.Root, fixture.State.Total,
                fixture.State.Attempt.ExecutionPlan.Sha256));
            Assert.Equal(0, fixture.Base.Count("execution_target_command_jobs"));
            Assert.False(Directory.Exists(Path.Combine(fixture.Directory, "observed")));
            Assert.False(File.Exists(fixture.Marker + ".main"));
        });
    }

    [Fact(DisplayName = "Cancellation During An Actual Post Hook Cannot Rewrite The Already Published Target Cleanup Decision")]
    public async Task FreezesCleanupDecision()
    {
        var fixture = new AndroidRuntimeFixture(configuration: true, hooks: true, postWait: true);
        await fixture.ExecuteAsync(async () =>
        {
            var running = fixture.Consumer.RunNextAsync();
            await fixture.RoleReadyAsync("post", TimeSpan.FromMinutes(6));
            var before = fixture.Collected();
            Assert.Equal(TargetCleanupOutcome.Completed, before.Cleanup.Outcome);
            Assert.Equal(TargetCleanupVerification.DeviceOffline, before.Cleanup.Verification);
            var cancelError = Record.Exception(fixture.Cancel);
            File.WriteAllText(fixture.Marker + ".post.release", fixture.Nonce);
            AttemptReceipt? receipt = null;
            var completionError = await Record.ExceptionAsync(async () => receipt = await running);
            Assert.Null(cancelError);
            Assert.Null(completionError);
            Assert.Equal(LifecycleOutcome.Cancelled, Assert.IsType<AttemptReceipt>(receipt).Facts.Lifecycle);
            var after = fixture.Collected();
            Assert.True(System.Text.Json.JsonElement.DeepEquals(
                System.Text.Json.JsonSerializer.SerializeToElement(before, StepResultCodec.Options),
                System.Text.Json.JsonSerializer.SerializeToElement(after, StepResultCodec.Options)));
            var state = fixture.State;
            var intent = Assert.Single(AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.NotEmpty(new AndroidResultEvidence(fixture.Source.Instance, fixture.Source.Database, new())
                .RequireCollected(state, intent, true));
            fixture.VerifyOriginal();
        });
    }
}
