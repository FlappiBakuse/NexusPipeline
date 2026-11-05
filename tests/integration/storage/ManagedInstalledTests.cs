using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Media;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ManagedInstalledTests
{
    [Fact(DisplayName = "The Original Installed Live Screenshot Entry Captures Only The Actual Selected Run And Closes Its Package Pins")]
    public async Task CapturesInstalledPreview()
    {
        using var inputs = new BuiltExtensionInputs("live-screenshot", "NEXUS_PACKAGED_SCREENSHOT_RECEIPT");
        var execution = new PcStageFixture(configuration: true, distinctConfiguration: true,
            hooks: false, mainWait: true, nativeWindow: true, accept: false);
        await execution.ExecuteAsync(async () =>
        {
            using var installed = new InstallRuntimeFixture(catalog: execution.Base.CreateCatalog(),
                portDependencies: new(null, null, null, execution.LiveMedia.Previews, execution.LiveMedia, execution.LiveMedia),
                payloadRoots: new(AppContext.BaseDirectory, Path.Combine(execution.Source.Instance.RuntimeRoot, "extensions")),
                originalManifest: inputs.Manifest, originalManifestBytes: inputs.ManifestBytes, packageArchive: inputs.Archive);
            try
            {
                var activation = await installed.Session().ApplyAsync(installed.Prepared, default);
                Assert.True(activation.IsEnabled); Assert.True(activation.IsInstalled);
                installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
                ExtensionProjectionPublication.PublishIfDirty(execution.Source.Instance, execution.Source.Database,
                    execution.Source.Lease, new(), installed.Roots, execution.Base.Resources, execution.Base.Index);
                Assert.Equal("live-screenshot", Assert.Single(installed.Activator.ReadPublishedScopes()).ExtensionId);
                RequirePinned(installed);
                execution.Accept(); var state = execution.State;
                var context = new RunMediaContext(execution.Source.Instance.InstanceId,
                    Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
                Assert.Throws<UnauthorizedAccessException>(() => execution.LiveMedia.OpenPreview(
                    PcStageFixture.Subject, context, "live-screenshot", "current-task"));
                var original = File.ReadAllBytes(execution.Source.Configuration);
                var running = execution.Consumer.RunNextAsync();
                try
                {
                    await execution.AwaitMainAsync();
                    await using var preview = await Open(execution, context);
                    Assert.Throws<UnauthorizedAccessException>(() => execution.LiveMedia.OpenPreview(
                        "another-server-subject", context, "live-screenshot", "current-task"));
                    Assert.Throws<UnauthorizedAccessException>(() => execution.LiveMedia.OpenPreview(
                        PcStageFixture.Subject, new(context.InstanceId, context.RunId, Guid.NewGuid()), "live-screenshot", "current-task"));
                    var first = await preview.CaptureAsync(); var second = await preview.CaptureAsync();
                    Assert.True(first.Width > 0 && first.Height is > 0 and <= 360); Assert.True(second.SizeBytes >= 33);
                    Assert.True(second.CapturedAt >= first.CapturedAt);
                    await preview.DisposeAsync();
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preview.CaptureAsync().AsTask());
                    await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
                    Assert.Empty(installed.Activator.ReadPublishedScopes()); RequireReleased(installed);
                    Assert.Throws<InvalidOperationException>(() => execution.LiveMedia.OpenPreview(
                        PcStageFixture.Subject, context, "live-screenshot", "current-task"));
                    File.WriteAllText(execution.HookMarker + ".main.release", "release");
                    var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(45)));
                    Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
                    Assert.Equal(original, File.ReadAllBytes(execution.Source.Configuration));
                    Assert.Equal(0, execution.Consumer.PendingReconciliationCount); inputs.VerifyUnchanged();
                }
                finally
                {
                    File.WriteAllText(execution.HookMarker + ".main.release", "release");
                    await running.WaitAsync(TimeSpan.FromSeconds(45));
                }
            }
            finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
        });
    }

    [Fact(DisplayName = "The Original Installed Game Check In Entry Registers Its Schedule And Closes Without Inventing A Planned Invocation")]
    public async Task RegistersInstalledSchedule()
    {
        using var inputs = new BuiltExtensionInputs("game-checkin", "NEXUS_PACKAGED_CHECKIN_RECEIPT");
        using var execution = new ExecutionAdmissionFixture(); var source = execution.Source;
        var schedules = new PersistentScheduleService(source.Instance, source.Database, source.Lease, execution.Resources,
            execution.Lifecycle, execution.Index, execution.Service);
        var secrets = new SecretFieldService(source.Instance, source.Database, source.Lease, execution.Resources, execution.Index, new());
        var secretPolicy = new SecretAccessPolicy(source.Instance, source.Database, source.Lease, execution.Resources);
        using var http = new CheckInHttp(200);
        using var installed = new InstallRuntimeFixture(managedMode: "formal-game-checkin",
            catalog: execution.CreateCatalog(), packageArchive: inputs.Archive,
            portDependencies: new(schedules, secrets, secretPolicy, null, null, null, () => new HttpClient(http, disposeHandler: false)),
            originalManifest: inputs.Manifest, originalManifestBytes: inputs.ManifestBytes);
        try
        {
            var activation = await installed.Session().ApplyAsync(installed.Prepared, default);
            Assert.True(activation.IsInstalled); Assert.True(activation.IsEnabled); Assert.NotNull(activation.ActivationId);
            installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
            Assert.Equal("game-checkin", Assert.Single(installed.Activator.ReadPublishedScopes()).ExtensionId);
            RequirePinned(installed);
            await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(installed.Activator.ReadPublishedScopes()); RequireReleased(installed);
            var work = new List<ScheduledProviderState>(); ScheduledProviderInventory.Visit(source.Instance, source.Database, new(), work.Add);
            Assert.Empty(work); inputs.VerifyUnchanged();
        }
        finally
        {
            try { await schedules.StopAsync(); }
            finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    private static void RequirePinned(InstallRuntimeFixture installed) => Assert.Throws<IOException>(() =>
    { using var denied = new FileStream(Path.Combine(installed.Target, "manifest.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
    private static void RequireReleased(InstallRuntimeFixture installed)
    {
        using var file = new FileStream(Path.Combine(installed.Target, "manifest.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(installed.Package.ManifestBytes.LongLength, file.Length);
    }
    private static async Task<ExecutionLivePreview> Open(PcStageFixture execution, RunMediaContext context)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { return execution.LiveMedia.OpenPreview(PcStageFixture.Subject, context, "live-screenshot", "current-task"); }
            catch (UnauthorizedAccessException) when (clock.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(20); }
        }
    }
}
