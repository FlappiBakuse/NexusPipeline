using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PreferenceViewTests
{
    [Fact(DisplayName = "The Already Running Settings View Observes The Actual First Install Preference And Keeps Its Frozen Startup Values")]
    public async Task ObservesInstalledPreference()
    {
        await using var fixture = new PackageServiceFixture(enabled: null);
        var owner = fixture.Package.Catalog; var defaults = new HostSettingsStore(owner.Database).Read();
        var settings = new HostSettingsService(owner.Instance, owner.Database, owner.Lease, owner.Resources,
            new(DesktopMode.Browser, defaults.Values.Service, defaults.Values.Updates.PluginSource));
        Assert.Empty(settings.Read().Saved.Values.Extensions); Assert.Equal(0, settings.Read().Saved.Revision);
        await fixture.Runtime.StartAsync(default);
        var catalog = await fixture.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        var accepted = await fixture.Service.ApplyAsync(Guid.NewGuid(), "local", fixture.Command(catalog), default);
        await fixture.WaitCompletedAsync(accepted.OperationId);
        var current = settings.Read();
        Assert.Equal(1, current.Saved.Revision); Assert.True(Assert.Single(current.Saved.Values.Extensions).Enabled);
        Assert.True(Assert.Single(current.Effective.Extensions).Enabled);
        Assert.Equal(DesktopMode.Browser, current.Effective.Desktop.Mode);
        Assert.Equal(DesktopMode.Desktop, current.Saved.Values.Desktop.Mode);
        Assert.Contains(SettingsRestartReason.DesktopMode, current.RestartReasons);
        Assert.NotNull(Assert.Single(fixture.Runtime.ReadStates()).ActivationId);
        var change = new HostSettingsChange(current.Saved.Revision, current.Saved.Values with { Locale = HostLocale.EnUs });
        _ = settings.Save(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
        var next = settings.Read(); Assert.Equal(2, next.Saved.Revision); Assert.Equal(HostLocale.EnUs, next.Effective.Locale);
        Assert.True(Assert.Single(next.Effective.Extensions).Enabled);
        Assert.Equal(DesktopMode.Browser, next.Effective.Desktop.Mode);
    }
}
