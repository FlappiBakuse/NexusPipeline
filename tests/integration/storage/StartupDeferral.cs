using Microsoft.Extensions.Hosting;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class StartupDeferral
{
    internal static async Task PrepareAsync(MaintenanceBinaryFixture fixture)
    {
        var contracts = new ContractReader();
        var store = new HostSettingsStore(fixture.Database);
        var saved = store.Read();
        var change = new HostSettingsChange(saved.Revision, saved.Values with
        {
            Desktop = saved.Values.Desktop with { Mode = DesktopMode.Browser, OpenOnStartup = false },
            Updates = saved.Values.Updates with { CheckEnabled = true, AutoApplyEnabled = true, Channel = UpdateChannel.Stable }
        });
        store.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "startup-fixture", change), change, DateTimeOffset.UtcNow);
        saved = store.Read();
        using var lease = InstanceLease.Acquire(fixture.Instance, contracts);
        var roots = new PayloadApplyRoots(Path.GetDirectoryName(fixture.HostPath)!, Path.Combine(fixture.Instance.RuntimeRoot, "extensions"));
        var index = new StartupJournalRecovery(fixture.Instance, contracts, fixture.Database, roots).Recover(lease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        var settings = new HostSettingsService(fixture.Instance, fixture.Database, lease, resources,
            new(saved.Values.Desktop.Mode, saved.Values.Service, saved.Values.Updates.PluginSource));
        await using var updates = new HostUpdateService(fixture.Instance, fixture.Database, lease, resources, contracts,
            roots, UpdateReleaseTrust.Configured(), fixture.CreateUpdateClient);
        await using var extensions = new ExtensionRuntimeLifecycle(fixture.Instance, fixture.Database, lease, contracts,
            roots, resources, index, new ExecutionProviderRegistry());
        await using var packages = new ExtensionPackageService(fixture.Instance, fixture.Database, lease, contracts,
            roots, resources, index, extensions, createClient: null);
        using var externalDesktop = new NativeClientBinding(Environment.ProcessId);
        var lifetime = new Lifetime();
        await using var maintenance = new HostMaintenanceController(fixture.Instance, fixture.Database, lease, contracts,
            roots, lifecycle, resources, lifetime, externalDesktop, inheritedOrigin: null);
        await using var automatic = new HostAutomaticUpdates(fixture.Instance, fixture.Database, settings, updates,
            maintenance, packages, extensions, CancellationToken.None);
        Assert.False(await automatic.ApplyStartupAsync());
        Assert.Equal("automatic_startup_update_blocked", automatic.Status.FailureCode);
        Assert.Equal("0.18.1", automatic.Status.AvailableHostVersion);
        var operation = Assert.IsType<Guid>(automatic.Status.PendingOperationId);
        var pending = maintenance.Read(operation, HostAutomaticUpdates.Subject);
        Assert.NotNull(pending);
        Assert.Equal(HostUpdateTiming.NextStartup, pending.Timing);
        Assert.Equal(HostUpdateState.Pending, pending.State);
        Assert.Null(pending.JournalId);
        Assert.False(lifetime.Stopped);
        Assert.Equal("original", File.ReadAllText(Path.Combine(roots.HostPayloadRoot, "README.md")));
        fixture.UseOperation(operation, HostAutomaticUpdates.Subject);
        var source = new UpdateSourceCodec(fixture.Instance, contracts).ReadMetadata(fixture.Plan.MaintenanceSource!);
        Assert.Equal(UpdateWindowMode.Headless, source.Window.Mode);
        Assert.False(source.Window.Visible);
        Console.WriteLine($"startup discovery=0.18.1; handoff=desktop_family_proof_required; persisted=NextStartup; operation={operation:D}");
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        internal bool Stopped { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped = true;
    }
}
