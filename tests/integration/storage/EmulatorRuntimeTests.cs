using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EmulatorRuntimeTests
{
    [Theory(DisplayName = "The Original Emulator Provider Selects A Physical Instance And Actual Host Commands Launch Capture And Shut Down The Controlled Vendor Before Final Collection")]
    [InlineData("ldplayer")]
    [InlineData("nox")]
    [InlineData("bluestacks")]
    public async Task RunsVendorStage(string vendor)
    {
        using var fixture = new EmulatorRuntimeFixture(vendor);
        _ = fixture.Start(); var receipt = await fixture.Runtime.RunAsync();
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
        var state = fixture.State; var reader = new AndroidControlEvidence(fixture.Runtime.Source.Instance, fixture.Runtime.Source.Database, new());
        var registrations = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var launch = Assert.Single(registrations, value => value.Action == ExecutionAndroidAction.LaunchDevice);
        var intent = Assert.Single(AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        var collected = reader.Read<AndroidCollectedDocument>(intent.Artifact);
        Assert.Equal(TargetCleanupVerification.DeviceOffline, collected.Cleanup.Verification);
        var launchCollection = reader.Collection(state, launch, collected.Commands.Single(value => value.Binding == launch.Binding).Collection, true);
        var native = reader.Observation(state, launch, launchCollection.Observation, true);
        Assert.Null(launchCollection.Retention); Assert.Equal(0U, launchCollection.Inventory.ActiveProcesses);
        Assert.NotEmpty(launchCollection.Inventory.AllFamily);
        if (vendor == "bluestacks") { Assert.Equal(AndroidResponseCompletion.OwnedLaunchHandoff, native.Completion); Assert.Equal(EngineOutcome.Unknown, native.Engine); }
        else Assert.Equal(AndroidResponseCompletion.NaturalRootExitAndEofBeforeStop, native.Completion);
        var capture = Assert.Single(ExecutionCaptureCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        Assert.NotNull(capture.Result);
        new AndroidControlEvidence(fixture.Runtime.Source.Instance, fixture.Runtime.Source.Database, new())
            .RequireStopped(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, launch.Binding.StepId);
    }

    [Theory(DisplayName = "A Vendor Launch Job Remains Owned Until Main Is Collected And Then Transfers Its Actual Living Family Without Terminating A Retained VM")]
    [InlineData("ldplayer")]
    [InlineData("nox")]
    [InlineData("bluestacks")]
    public async Task RetainsOwnedVm(string vendor)
    {
        using var fixture = new EmulatorRuntimeFixture(vendor, close: false, mainWait: true);
        fixture.Start(); var running = fixture.Runtime.Consumer.RunNextAsync();
        try
        {
            await fixture.Runtime.MainReadyAsync();
            Assert.False(running.IsCompleted);
            if (vendor == "bluestacks") RequireHandoff(fixture);
            File.WriteAllText(fixture.Runtime.Marker + ".main.release", "release");
            var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(90)));
            Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
            var state = fixture.State; var reader = new AndroidControlEvidence(fixture.Runtime.Source.Instance, fixture.Runtime.Source.Database, new());
            var launch = Assert.Single(AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256), value => value.Action == ExecutionAndroidAction.LaunchDevice);
            var result = reader.Read<AndroidCollectedDocument>(Assert.Single(AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Artifact);
            var collection = reader.Collection(state, launch, result.Commands.Single(value => value.Binding == launch.Binding).Collection, true);
            Assert.NotNull(collection.Retention); Assert.True(collection.Inventory.ActiveProcesses > 0); Assert.Equal(0U, collection.Inventory.LimitFlags);
            Assert.All(collection.Inventory.LivingFamily, process => Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(process)));
            reader.RequireStopped(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, launch.Binding.StepId);
        }
        finally { File.WriteAllText(fixture.Runtime.Marker + ".main.release", "release"); await running.WaitAsync(TimeSpan.FromSeconds(90)); }
    }

    private static void RequireHandoff(EmulatorRuntimeFixture fixture)
    {
        var state = fixture.State; var reader = new AndroidControlEvidence(fixture.Runtime.Source.Instance, fixture.Runtime.Source.Database, new());
        var startup = reader.Read<AndroidStartupDocument>(Assert.Single(AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Artifact);
        var command = Assert.Single(startup.Commands, value => value.Registration.Action == ExecutionAndroidAction.LaunchDevice);
        var original = reader.Observation(state, command.Registration, command.Observation, true);
        var blobs = new ImmutableBlobStore(fixture.Runtime.Source.Instance, new());
        BlobReference Save(AndroidCommandObservation value) => blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(value, StepResultCodec.Options));
        Assert.Throws<IOException>(() => reader.Observation(state, command.Registration, Save(original with { Root = null }), true));
        Assert.Throws<IOException>(() => reader.Observation(state, command.Registration, Save(original with { Root = original.Root! with { ProcessId = Environment.ProcessId } }), true));
        Assert.Throws<IOException>(() => reader.Observation(state, command.Registration, Save(original with { ExitCode = 0 }), true));
        var unknownReference = Save(original with { Completion = AndroidResponseCompletion.Unverified });
        var unknown = reader.Observation(state, command.Registration, unknownReference, true);
        Assert.Equal(ExecutionAndroidState.Unknown, ExecutionAndroidObservations.ReadSaved(reader, reader.Request(state, command.Registration), unknown, unknownReference).State);
    }

    [Fact(DisplayName = "Cancellation Stops The Actual Hanging Readonly Vendor Enumeration Family Before Any VM Launch And Preserves The Existing Unverified Refusal Path")]
    public async Task CancelsSourceRead()
    {
        using var fixture = new EmulatorRuntimeFixture("ldplayer", close: false);
        var source = fixture.Runtime.Source; var catalog = source.Definitions.Read(); var script = catalog.Scripts.Single(value => value.Id == source.ScriptId);
        var changes = new NexusPipeline.Core.Automation.AutomationChanges(catalog.Revision, [],
            [script with { Limits = script.Limits with { TotalTimeoutSeconds = 0 } }], [], [], [], [], []);
        source.Definitions.Commit(NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore.Identity(Guid.NewGuid(), "emulator-fixture", changes), changes, DateTimeOffset.UtcNow);
        fixture.Start(); File.WriteAllText(Path.Combine(fixture.Runtime.Directory, "fixture-enumeration-delay"), fixture.Runtime.Nonce);
        var running = fixture.Runtime.Consumer.RunNextAsync();
        var marker = Path.Combine(fixture.Runtime.Directory, "fixture-enumeration-wait.json");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(marker)) { if (running.IsCompleted) await running; await Task.Delay(20, deadline.Token); }
            fixture.Cancel();
            var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            Assert.False(File.Exists(Path.Combine(fixture.Runtime.Directory, "fixture-vm.json")));
            using var saved = JsonDocument.Parse(File.ReadAllBytes(marker));
            Assert.Equal(fixture.Runtime.Nonce, saved.RootElement.GetProperty("nonce").GetString());
            var control = Assert.Single(fixture.State.Total.Steps).Target!.Android!.Emulator!.Control;
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(new(saved.RootElement.GetProperty("processId").GetInt32(),
                saved.RootElement.GetProperty("createdAt").GetDateTime(), control.Path, control.Sha256, "", false)));
        }
        finally { if (!running.IsCompleted) fixture.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Fact(DisplayName = "Ordinary Atomic Vendor Configuration Updates Preserve Physical Generation While A Recreated VM Directory Or Reassigned Port Is Refused")]
    public void RequiresPhysicalGeneration()
    {
        using var fixture = new EmulatorRuntimeFixture("bluestacks");
        var source = fixture.Runtime.Source; var script = source.Definitions.Read().Scripts.Single(value => value.Id == source.ScriptId);
        var captured = fixture.Emulators.Capture(script, source.Definitions.Read().Roots);
        var path = Path.Combine(fixture.Runtime.Directory, "bluestacks.conf");
        File.WriteAllText(path + ".next", File.ReadAllText(path) + "bst.instance.Pie64.status=\"changed\"\n"); File.Move(path + ".next", path, true);
        fixture.Emulators.RequireRuntime(new() { Driver = "genericAdb", Tool = ExecutionHookImages.Capture(fixture.Adb, true),
            Endpoint = fixture.Selected.Endpoint, Package = AndroidRuntimeFixture.Package, StartArguments = [], Emulator = captured.Target }, source.ScriptId);
        Directory.Move(fixture.VmDirectory, fixture.VmDirectory + "-retired"); Directory.CreateDirectory(fixture.VmDirectory);
        Assert.Throws<IOException>(() => fixture.Emulators.RequireCurrent(captured.Target, script));
        Directory.Delete(fixture.VmDirectory); Directory.Move(fixture.VmDirectory + "-retired", fixture.VmDirectory);
        File.WriteAllText(path, File.ReadAllText(path).Replace("16384", "16385", StringComparison.Ordinal));
        var refused = Assert.ThrowsAny<NexusPipeline.Extensibility.Emulation.EmulatorException>(() => fixture.Emulators.Capture(script, source.Definitions.Read().Roots));
        Assert.Equal("emulator_endpoint_mismatch", refused.Code);
    }
}
