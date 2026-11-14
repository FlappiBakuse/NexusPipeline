using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class OfflineRemovalTests
{
    [Fact]
    public async Task WaitsForMissingPluginResources()
    {
        await using var fixture = new InstallationCrashFixture("committed");
        await fixture.StartAsync(); Assert.True((await fixture.StopAsync()).CompleteJobStopped);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        await using var runtime = new ExtensionRuntimeLifecycle(fixture.Instance, database, lease, new(),
            fixture.Roots, resources, index, new());
        using (resources.TryAcquire(Guid.NewGuid(), [new("extension:fixture-plugin", ResourceClaimMode.Reference)]).Lease!)
        {
            Directory.Delete(Path.Combine(fixture.Roots.ExtensionPayloadRoot, "fixture-plugin"), recursive: true);
            await runtime.ReconcileFilesAsync(default);
            var waiting = Assert.Single(runtime.ReadStates());
            Assert.True(waiting.IsInstalled); Assert.Equal("extension_files_removed", waiting.UnavailableReason);
        }
        await runtime.ReconcileFilesAsync(default);
        Assert.False(Assert.Single(runtime.ReadStates()).IsInstalled);
        await runtime.ReconcileFilesAsync(default);
        Assert.Equal(2, Assert.Single(runtime.ReadStates()).InstalledRevision);
    }

    [Fact(DisplayName = "A Real Cold Installation Can Be Removed Without Provisioning New Package Trust Or Contacting A Network")]
    public async Task RemovesWithoutNetwork()
    {
        await using var fixture = new InstallationCrashFixture("committed");
        await fixture.StartAsync(); Assert.True((await fixture.StopAsync()).CompleteJobStopped);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        await using var runtime = new ExtensionRuntimeLifecycle(fixture.Instance, database, lease, new(),
            fixture.Roots, resources, index, new());
        await runtime.StartAsync(default);
        var original = Assert.Single(runtime.ReadStates()); Assert.True(original.IsInstalled); Assert.Null(original.ActivationId);
        Assert.Equal("extension_release_trust_unprovisioned", original.UnavailableReason);
        var settings = new HostSettingsStore(database).Read();
        await using var packages = new ExtensionPackageService(fixture.Instance, database, lease, new(), fixture.Roots,
            resources, index, runtime, (Func<HttpClient>?)null);
        Assert.False(packages.HasReleaseTrust);
        var id = Guid.NewGuid(); var command = new ExtensionApplyCommand(ExtensionApplyAction.Remove,
            "fixture-plugin", 1, settings.Revision, null, null);
        var accepted = await packages.ApplyAsync(id, "local", command, default);
        var queries = new ExtensionQueryService(fixture.Instance, lease, database, new(), fixture.Roots);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        ExtensionApplyState result;
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            result = queries.Read(id, "local")!;
            if (result.Outcome == ExtensionApplyOutcome.Removed) break;
            await Task.Delay(25, timeout.Token);
        }
        Assert.Equal(accepted.JournalId, result.Operation.InitialJournalId); Assert.Equal(2, result.ResultInstalledRevision);
        Assert.False(result.ResultIsInstalled); Assert.Null(result.HistoricalActivationId);
        var state = Assert.Single(runtime.ReadStates()); Assert.False(state.IsInstalled); Assert.Null(state.ActivationId);
        Assert.Null(state.UnavailableReason);
        var replay = await packages.ApplyAsync(id, "local", command, default); Assert.Equal("completed", replay.Status);
        Assert.Equal(accepted.JournalId, replay.JournalId);
        Assert.Equal(settings.Revision, new HostSettingsStore(database).Read().Revision);
        Assert.False(Directory.Exists(Path.Combine(fixture.Roots.ExtensionPayloadRoot, "fixture-plugin")));
    }
}
