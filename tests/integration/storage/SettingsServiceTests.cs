using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class SettingsServiceTests
{
    [Fact(DisplayName = "Only Committed Settings Are Published And Replaying An Older Save Does Not Rewind The Running View")]
    public void PublishesCommittedSettings()
    {
        using var fixture = Create();
        var first = new HostSettingsChange(0, HostSettings.Default with { HistoryRetentionDays = 20,
            Service = HostSettings.Default.Service with { WebPort = 60000 } });
        var identity = Identity(first);
        fixture.Service.Save(identity, first, DateTimeOffset.UtcNow);
        Assert.Equal(60000, fixture.Service.Read().Saved.Values.Service.WebPort);
        Assert.Equal(58731, fixture.Service.Read().Effective.Service.WebPort);
        var second = new HostSettingsChange(1, first.Values with { HistoryRetentionDays = 40 });
        fixture.Service.Save(Identity(second), second, DateTimeOffset.UtcNow);
        Assert.False(fixture.Service.Save(identity, first, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(40, fixture.Service.Read().Effective.HistoryRetentionDays);
        var rejected = new HostSettingsChange(0, HostSettings.Default);
        Assert.Throws<HostSettingsException>(() => fixture.Service.Save(Identity(rejected), rejected, DateTimeOffset.UtcNow));
        Assert.Equal(2, fixture.Service.Read().Saved.Revision);
        Assert.Equal(2, new HostSettingsStore(fixture.Database).Read().Revision);
    }

    [Theory(DisplayName = "Settings Mutation Uses The Actual Shared Admission And Cannot Publish While Its Host Is Unavailable")]
    [InlineData("conflict")]
    [InlineData("recovery")]
    [InlineData("closing")]
    [InlineData("expired-instance")]
    public void RequiresSettingsAdmission(string fault)
    {
        using var fixture = Create();
        using var conflict = fault == "conflict"
            ? fixture.Resources.TryAcquire(Guid.NewGuid(), [new("settings:host", ResourceClaimMode.Mutation)]).Lease : null;
        if (fault == "recovery") fixture.Index.BlockUnknown("fixture_unknown");
        if (fault == "closing") fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        if (fault == "expired-instance") fixture.Lease.Dispose();
        var change = new HostSettingsChange(0, HostSettings.Default with { HistoryRetentionDays = 30 });
        var error = Record.Exception(() => fixture.Service.Save(Identity(change), change, DateTimeOffset.UtcNow));
        Assert.NotNull(error);
        if (fault != "expired-instance") Assert.Equal(fault == "conflict" ? "settings_resource_conflict" : "settings_host_unavailable", error.Message);
        Assert.Equal(0, new HostSettingsStore(fixture.Database).Read().Revision);
        Assert.Equal(0, fixture.Service.Read().Saved.Revision);
    }

    [Theory(DisplayName = "Plugin Preferences Can Change During Unrelated Runs But Keep Dependency And Settings Leases")]
    [InlineData("settings:host", ResourceClaimMode.Reference, true)]
    [InlineData("settings:host", ResourceClaimMode.Mutation, false)]
    [InlineData("extension:live-screenshot", ResourceClaimMode.Reference, false)]
    [InlineData("extension:other-provider", ResourceClaimMode.Reference, true)]
    [InlineData("settings:extension-preferences", ResourceClaimMode.Mutation, false)]
    public void SeparatesExtensionPreferences(string resource, ResourceClaimMode mode, bool allowed)
    {
        using var fixture = Create();
        using var conflict = fixture.Resources.TryAcquire(Guid.NewGuid(), [new(resource, mode)]).Lease;
        Assert.NotNull(conflict);
        var change = new HostSettingsChange(0, HostSettings.Default with { Extensions = [new("live-screenshot", false)] });
        if (allowed)
        {
            fixture.Service.Save(Identity(change), change, DateTimeOffset.UtcNow);
            Assert.Equal(Assert.Single(change.Values.Extensions), Assert.Single(fixture.Service.Read().Effective.Extensions));
            var stale = change with { Values = change.Values with { Extensions = [new("live-screenshot", true)] } };
            Assert.Throws<HostSettingsException>(() => fixture.Service.Save(Identity(stale), stale, DateTimeOffset.UtcNow));
            Assert.False(fixture.Service.Read().Saved.Values.Extensions.Single().Enabled);
        }
        else
        {
            Assert.Throws<HostSettingsException>(() => fixture.Service.Save(Identity(change), change, DateTimeOffset.UtcNow));
            Assert.Equal(0, fixture.Service.Read().Saved.Revision);
        }
    }

    [Theory(DisplayName = "Preference Saves Preserve Host Recovery And Closing Admission")]
    [InlineData("recovery")]
    [InlineData("closing")]
    [InlineData("mixed")]
    public void KeepsPreferenceAdmission(string fault)
    {
        using var fixture = Create();
        using var running = fixture.Resources.TryAcquire(Guid.NewGuid(), [new("settings:host", ResourceClaimMode.Reference)]).Lease;
        Assert.NotNull(running);
        if (fault == "recovery") fixture.Index.BlockUnknown("fixture_unknown");
        if (fault == "closing") fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        var values = HostSettings.Default with { Extensions = [new("live-screenshot", false)] };
        if (fault == "mixed") values = values with { HistoryRetentionDays = 30 };
        var change = new HostSettingsChange(0, values);
        Assert.Throws<HostSettingsException>(() => fixture.Service.Save(Identity(change), change, DateTimeOffset.UtcNow));
        Assert.Equal(0, fixture.Service.Read().Saved.Revision);
    }

    [Theory(DisplayName = "Preference Admission Waits For Actual Release And Rechecks Cancellation Authority And Revision")]
    [InlineData("released")]
    [InlineData("held")]
    [InlineData("cancelled")]
    [InlineData("authority")]
    [InlineData("revision")]
    public async Task WaitsForReaders(string outcome)
    {
        using var fixture = Create();
        using var reader = fixture.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:live-screenshot", ResourceClaimMode.Reference)]).Lease!;
        Assert.NotNull(reader);
        using var cancellation = new CancellationTokenSource();
        var authorized = true;
        var change = new HostSettingsChange(0, HostSettings.Default with { Extensions = [new("live-screenshot", false)] });
        var identity = Identity(change);
        var saving = fixture.Service.SaveAsync(identity, change, DateTimeOffset.UtcNow,
            () => { if (!authorized) throw new UnauthorizedAccessException(); }, cancellation.Token);
        Assert.False(saving.IsCompleted); Assert.Equal(0, fixture.Service.Read().Saved.Revision);
        if (outcome == "cancelled") cancellation.Cancel();
        if (outcome == "authority") authorized = false;
        if (outcome == "revision")
        {
            var other = new HostSettingsChange(0, HostSettings.Default with { HistoryRetentionDays = 30 });
            fixture.Service.Save(Identity(other), other, DateTimeOffset.UtcNow);
        }
        if (outcome != "held") reader.Dispose();
        if (outcome == "released")
        {
            var result = await saving.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(Assert.Single(result.Saved.Values.Extensions).Enabled);
            Assert.False((await fixture.Service.SaveAsync(identity, change, DateTimeOffset.UtcNow, () => { }, default)).IsNew);
        }
        else
        {
            var error = await Record.ExceptionAsync(async () => await saving);
            if (outcome == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
            else if (outcome == "authority") Assert.IsType<UnauthorizedAccessException>(error);
            else Assert.Equal(outcome == "held" ? "settings_resource_conflict" : "settings_revision_conflict",
                Assert.IsType<HostSettingsException>(error).Message);
            Assert.Empty(fixture.Service.Read().Saved.Values.Extensions);
            Assert.Equal(outcome == "revision" ? 1 : 0, fixture.Service.Read().Saved.Revision);
        }
    }

    private static OperationIdentity Identity(HostSettingsChange change) => HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change);

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "settings-service-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var lease = InstanceLease.Acquire(instance, new());
        try
        {
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
            var lifecycle = new HostLifecycle(index, TimeProvider.System);
            var resources = new ResourceLeaseManager(lifecycle);
            var value = HostSettings.Default;
            var service = new HostSettingsService(instance, database, lease, resources,
                new(value.Desktop.Mode, value.Service, value.Updates.PluginSource));
            return new(database, lease, index, lifecycle, resources, service);
        }
        catch { lease.Dispose(); throw; }
    }

    private sealed record Fixture(StateDatabase Database, InstanceLease Lease, RecoveryIndex Index, HostLifecycle Lifecycle,
        ResourceLeaseManager Resources, HostSettingsService Service) : IDisposable
    {
        public void Dispose() => Lease.Dispose();
    }
}
