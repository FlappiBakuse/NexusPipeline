using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidMuMuTests
{
    [Fact(DisplayName = "Explicit Prior Discovery And Current Saved Info Bind The Same Manager Index Port And Every Actual Command")]
    public async Task BindsCurrentDiscovery()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, configuration: true);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var state = fixture.State;
            var commands = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            Assert.Equal(ExecutionAndroidAction.DeviceState, commands[0].Action);
            var mapping = Assert.Single(state.Total.Steps).Target!.Android!.Mapping!;
            Assert.Equal(0, mapping.Index); Assert.Equal(16384, mapping.EndpointPort);
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            foreach (var command in commands)
            {
                var frozen = reader.Request(state, command);
                Assert.Equal(mapping.Index.ToString(), frozen.Command.Arguments[2]);
                Assert.Equal("-v", frozen.Command.Arguments[1]);
                Assert.Equal(mapping.ManagerImageSha256, frozen.Tool.Sha256);
                var collection = reader.Collection(state, command,
                    Assert.Single(fixture.Collected().Commands, item => item.Binding == command.Binding).Collection, true);
                Assert.All(collection.Inventory.AllFamily, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            }
            Assert.Equal(TargetCleanupVerification.DeviceOffline, fixture.Collected().Cleanup.Verification);
        });
    }

    [Fact(DisplayName = "A Changed Current Manager Port Cannot Authorize Launch Main Or Any Cleanup Effect Despite The Earlier Valid Discovery")]
    public async Task RejectsChangedPorts()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, forceClose: true);
        await fixture.ExecuteAsync(async () =>
        {
            fixture.ChangeReportedPort(16385);
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
            Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
            var state = fixture.State;
            var commands = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            Assert.NotEmpty(commands); Assert.All(commands, command => Assert.Equal(ExecutionAndroidAction.DeviceState, command.Action));
            Assert.Equal(TargetCleanupOutcome.Unverified, fixture.Collected().Cleanup.Outcome);
            Assert.Equal(TargetCleanupVerification.None, fixture.Collected().Cleanup.Verification);
            Assert.False(File.Exists(fixture.Marker + ".main"));
        });
    }

    [Fact(DisplayName = "Historical Target Results Replay Only Saved Responses After The Mutable Device Fixture And Command Markers Disappear")]
    public async Task ReplaysSavedResponses()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, forceClose: false);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.RunAsync(); var state = fixture.State;
            var reader = new AndroidResultEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var intent = Assert.Single(AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            var before = reader.RequireCollected(state, intent, true).ToArray();
            File.Delete(Path.Combine(fixture.Directory, "fixture-device.json"));
            Directory.Delete(Path.Combine(fixture.Directory, "observed"), recursive: true);
            Assert.Equal(before, reader.RequireCollected(state, intent, true));
            Assert.Equal(TargetExecutionOutcome.ControlledReady,
                Assert.Single(AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Outcome);
        });
    }

    [Fact(DisplayName = "Launch Disabled Still Obtains Fresh Cleanup Mapping Before The Explicit Final Shutdown")]
    public async Task RefreshesCleanupMapping()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, launch: false, forceClose: true);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync(); Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var state = fixture.State;
            var startup = Assert.Single(AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal(TargetExecutionOutcome.Skipped, startup.Outcome); Assert.Empty(startup.Commands);
            var commands = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            Assert.Equal(AndroidCommandPurpose.CleanupReadiness, commands[0].Purpose);
            Assert.Equal(ExecutionAndroidAction.DeviceState, commands[0].Action);
            Assert.Equal(ExecutionAndroidAction.Shutdown, commands[1].Action);
            Assert.Equal(TargetCleanupVerification.DeviceOffline, fixture.Collected().Cleanup.Verification);
        });
    }

    [Fact(DisplayName = "A Port Changed During The Actual Main Cannot Borrow Its Startup Info For Later Cleanup Effects")]
    public async Task RejectsStaleMapping()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, forceClose: true, mainWait: true);
        await fixture.ExecuteAsync(async () =>
        {
            var running = fixture.Consumer.RunNextAsync();
            await fixture.MainReadyAsync(); fixture.ChangeReportedPort(16385); fixture.Cancel();
            var receipt = Assert.IsType<AttemptReceipt>(await running);
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var state = fixture.State;
            var commands = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            var tail = commands.Where(command => AndroidControlAdmission.IsCleanup(command.Purpose)).ToArray();
            var only = Assert.Single(tail);
            Assert.Equal(AndroidCommandPurpose.CleanupReadiness, only.Purpose);
            Assert.Equal(TargetCleanupOutcome.Unverified, fixture.Collected().Cleanup.Outcome);
            fixture.VerifyOriginal();
        });
    }
}
