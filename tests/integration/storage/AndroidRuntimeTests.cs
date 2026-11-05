using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidRuntimeTests
{
    [Theory(DisplayName = "Actual Explicit Device Commands Run Before Main And Complete Before Post And Original Configuration Restore")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdersDeviceCommands(bool hooks)
    {
        var fixture = new AndroidRuntimeFixture(configuration: true, hooks: hooks);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
            var state = fixture.State; var result = fixture.Collected();
            Assert.Equal(TargetCleanupOutcome.Completed, result.Cleanup.Outcome);
            Assert.Equal(TargetCleanupVerification.DeviceOffline, result.Cleanup.Verification);
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var registered = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            Assert.Equal(registered.Length, result.Commands.Length);
            Assert.All(registered, command =>
            {
                var request = reader.Request(state, command);
                if (request.Action == ExecutionAndroidAction.Connect) Assert.Equal(new[] { "connect", AndroidRuntimeFixture.Endpoint }, request.Command.Arguments);
                else Assert.Equal(new[] { "-s", AndroidRuntimeFixture.Endpoint }, request.Command.Arguments.Take(2));
                var stored = Assert.Single(result.Commands, item => item.Binding == command.Binding);
                var closure = reader.Collection(state, command, stored.Collection, true);
                Assert.Equal(0U, closure.Inventory.ActiveProcesses);
                Assert.Equal(closure.Inventory.AllFamily.Length, closure.Inventory.AllFamily.Select(member => (member.ProcessId, member.CreatedAtUtc)).Distinct().Count());
                Assert.All(closure.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            });
            var actual = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(fixture.Accepted.JournalId);
            var measured = JournalCapacityPolicy.Measure(state.Total);
            Assert.True(actual.Sum(item => item.Export().LongLength) <= measured.HistoryBytes);
        });
    }

    [Fact(DisplayName = "A Tool Root Can Naturally Exit And Reach Eof While Its Actual Server Child Still Requires Full Family Stop")]
    public async Task StopsServerChildren()
    {
        var fixture = new AndroidRuntimeFixture(serverAction: "connect", forceClose: false);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.RunAsync(); var state = fixture.State;
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var first = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)[0];
            var command = Assert.Single(fixture.Collected().Commands, item => item.Binding == first.Binding);
            var collection = reader.Collection(state, first, command.Collection, true);
            var observation = reader.Observation(state, first, collection.Observation, true);
            Assert.Equal(AndroidResponseCompletion.NaturalRootExitAndEofBeforeStop, observation.Completion);
            Assert.True(collection.Inventory.AllFamily.Length >= 2);
            Assert.All(collection.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        });
    }

    [Fact(DisplayName = "Explicit Offline Printed Before A Hang Cannot Become A Complete Response After Local Stop And Eof")]
    public async Task RejectsIncompleteOffline()
    {
        var fixture = new AndroidRuntimeFixture(responseAction: "offline", stderr: "error: device offline", exitCode: 1, hang: true);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.RunAsync(); var state = fixture.State; var result = fixture.Collected();
            Assert.Equal(TargetCleanupOutcome.Unverified, result.Cleanup.Outcome);
            Assert.Equal(TargetCleanupVerification.None, result.Cleanup.Verification);
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var last = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)[^1];
            var collection = reader.Collection(state, last, result.Commands[^1].Collection, true);
            var native = reader.Observation(state, last, collection.Observation, true);
            Assert.Equal(AndroidResponseCompletion.Unverified, native.Completion);
            Assert.True(native.OutputComplete); Assert.NotNull(native.ExitCode);
            Assert.Contains("device offline", reader.ReadText(native).StandardError);
            Assert.Equal(ExecutionAndroidState.Unknown, ExecutionAndroidObservations.ReadSaved(reader, reader.Request(state, last), native, collection.Observation).State);
        });
    }

    [Fact(DisplayName = "Daemon Failures Other Devices And Contradictory Responses Never Prove This Device Offline")]
    public async Task RejectsContradictoryDevices()
    {
        const string stdout = "ok", stderr = "error: device offline";
        const int code = 1;
        var fixture = new AndroidRuntimeFixture(responseAction: "offline", stdout: stdout, stderr: stderr, exitCode: code);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.RunAsync(); var state = fixture.State; var result = fixture.Collected();
            Assert.Equal(TargetCleanupOutcome.Unverified, result.Cleanup.Outcome);
            Assert.Empty(result.Cleanup.ConfirmationCommands);
            Assert.Equal(TargetCleanupVerification.None, result.Cleanup.Verification);
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var probes = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .Where(command => command.Action == ExecutionAndroidAction.OfflineProbe).ToArray();
            Assert.NotEmpty(probes);
            foreach (var probe in probes)
            {
                var saved = Assert.Single(result.Commands, item => item.Binding == probe.Binding);
                var collection = reader.Collection(state, probe, saved.Collection, true);
                var native = reader.Observation(state, probe, collection.Observation, true);
                Assert.True(native.StartReturned); Assert.NotNull(native.Root); Assert.True(native.OutputComplete);
                Assert.Equal(AndroidResponseCompletion.NaturalRootExitAndEofBeforeStop, native.Completion);
                Assert.Equal(code, native.ExitCode); Assert.Equal(EngineOutcome.Failed, native.Engine);
                var original = reader.ReadText(native);
                Assert.Equal(stdout, original.StandardOutput); Assert.Equal(stderr, original.StandardError);
                var interpreted = ExecutionAndroidObservations.ReadSaved(reader, reader.Request(state, probe), native, collection.Observation);
                Assert.Equal(ExecutionAndroidState.Unknown, interpreted.State);
                Assert.Equal(TargetCommandObservation.Contradictory, interpreted.Interpretation);
                Assert.Equal(0, ExecutionAndroidObservations.ConsecutiveOffline(1, interpreted));
                Assert.Contains(native.Root!, collection.Inventory.AllFamily);
                Assert.All(collection.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            }
        });
    }

    [Fact(DisplayName = "An Exit Zero Explicit Shutdown Error Preserves Its Native Exit But Contributes A Failed Cleanup")]
    public async Task PreservesShutdownFailures()
    {
        var fixture = new AndroidRuntimeFixture(responseAction: "shutdown", stdout: "Error: fixture shutdown denied", exitCode: 0);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync(); var state = fixture.State;
            Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
            var result = fixture.Collected(); Assert.Equal(TargetCleanupOutcome.Failed, result.Cleanup.Outcome);
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var last = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)[^1];
            var collection = reader.Collection(state, last, result.Commands[^1].Collection, true);
            var native = reader.Observation(state, last, collection.Observation, true);
            Assert.Equal(0, native.ExitCode); Assert.Equal(EngineOutcome.Succeeded, native.Engine);
        });
    }

    [Fact(DisplayName = "A Real Readiness Timeout Leaves Main Not Started And Its Business Unverified While Cleaning The Controlled Application")]
    public async Task CleansReadinessTimeout()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, responseAction: "foreground",
            stdout: "mCurrentFocus=Window{a u0 com.other.package/.Main}", waitSeconds: 1);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
            var state = fixture.State;
            var main = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            Assert.Equal(EngineOutcome.NotStarted, main.Latest.Engine); Assert.Equal(BusinessOutcome.Unverified, main.Latest.Business);
            var startup = Assert.Single(AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal(TargetExecutionOutcome.ReadinessTimedOut, startup.Outcome);
            Assert.Equal(TargetCleanupOutcome.Completed, fixture.Collected().Cleanup.Outcome);
            Assert.False(File.Exists(fixture.Marker + ".main"));
        });
    }

    [Fact(DisplayName = "Launch Disabled Records A Real Skipped Target Without Creating Any Tool Job Or Lending It Kill Authority")]
    public async Task SkipsDisabledLaunches()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, launch: false);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync(); Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var state = fixture.State;
            Assert.Empty(AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            var startup = Assert.Single(AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal(TargetExecutionOutcome.Skipped, startup.Outcome); Assert.Empty(startup.Commands);
            Assert.Empty(fixture.Collected().Commands); Assert.False(Directory.Exists(Path.Combine(fixture.Directory, "observed")));
        });
    }

    [Fact(DisplayName = "A Real Tool Output Overflow Preserves Bounded Original Bytes And Cannot Authorize Main Or A Complete Response")]
    public async Task BoundsToolOutput()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, overflowAction: "connect");
        await fixture.ExecuteAsync(async () =>
        {
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.Consumer.RunNextAsync());
            Assert.Equal(1, fixture.Consumer.PendingReconciliationCount);
            var state = fixture.State; var registered = Assert.Single(AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var collected = Assert.Single(fixture.Collected().Commands);
            var closure = reader.Collection(state, registered, collected.Collection, true);
            var observed = reader.Observation(state, registered, closure.Observation, true);
            Assert.False(observed.OutputComplete);
            Assert.InRange(observed.Parts.Sum(part => part.Content.SizeBytes), 1, AndroidControlCapacity.MaximumCommandRawBytes);
            Assert.All(closure.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            Assert.False(File.Exists(fixture.Marker + ".main"));
            Assert.Throws<IOException>(() => AndroidCollectedCodec.RequireResolved(state.Latest.Root, state.Total,
                state.Attempt.ExecutionPlan.Sha256, registered.Binding.StepId));
            Assert.Equal(1, fixture.Base.Count("execution_target_command_jobs"));
        });
    }

    [Fact(DisplayName = "Actual Nonzero Tool Exit And Original Raw Bytes Remain Independent Of The Unstarted Primary Business")]
    public async Task PreservesToolFailures()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, failAction: "connect");
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
            var state = fixture.State; var first = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)[0];
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var actual = Assert.Single(fixture.Collected().Commands, item => item.Binding == first.Binding);
            var collection = reader.Collection(state, first, actual.Collection, true);
            var observation = reader.Observation(state, first, collection.Observation, true);
            Assert.Equal(37, observation.ExitCode); Assert.Equal(EngineOutcome.Failed, observation.Engine);
            Assert.Contains("fixture rejected connect", reader.ReadText(observation).StandardError);
            Assert.NotEmpty(collection.Inventory.AllFamily);
            Assert.All(collection.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            Assert.False(File.Exists(fixture.Marker + ".main"));
        });
    }
}
