using NexusPipeline.Core.Settings;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class HostSettingsTests
{
    [Fact(DisplayName = "Saving Listener Desktop And Plugin Source Choices Does Not Pretend To Replace The Running Services")]
    public void PreservesEffectiveServices()
    {
        var before = HostSettings.Default;
        var state = new HostSettingsRuntime(new(0, before), new(before.Desktop.Mode, before.Service, before.Updates.PluginSource));
        var saved = new HostSettingsSnapshot(1, before with { LogLevel = HostLogLevel.Fatal, HistoryRetentionDays = 30,
            Desktop = before.Desktop with { Mode = DesktopMode.Browser }, Service = before.Service with { WebPort = 60000 },
            Updates = before.Updates with { PluginSource = PluginSourceChannel.Develop } });
        state.PublishSaved(saved);
        var observed = state.Read();
        Assert.Equal(60000, observed.Saved.Values.Service.WebPort);
        Assert.Equal(58731, observed.Effective.Service.WebPort);
        Assert.Equal(DesktopMode.Desktop, observed.Effective.Desktop.Mode);
        Assert.Equal(PluginSourceChannel.Stable, observed.Effective.Updates.PluginSource);
        Assert.Equal(HostLogLevel.Fatal, observed.Effective.LogLevel);
        Assert.Equal(30, observed.Effective.HistoryRetentionDays);
        Assert.Equal(new[] { SettingsRestartReason.WebListener, SettingsRestartReason.DesktopMode, SettingsRestartReason.PluginSource }, observed.RestartReasons);
        state.PublishSaved(new(0, before));
        Assert.Equal(1, state.Read().Saved.Revision);
        state.PublishDesktopMode(DesktopMode.Browser);
        Assert.Equal(DesktopMode.Browser, state.Read().Effective.Desktop.Mode);
        Assert.DoesNotContain(SettingsRestartReason.DesktopMode, state.Read().RestartReasons);
        Assert.Equal(58731, state.Read().Effective.Service.WebPort);
        Assert.Throws<HostSettingsException>(() => state.PublishDesktopMode((DesktopMode)99));
        Assert.Equal(DesktopMode.Browser, state.Read().Effective.Desktop.Mode);
    }

    [Fact(DisplayName = "Default Settings Preserve The Declared Desktop Listener Retention And Update Choices")]
    public void KeepsDeclaredDefaults()
    {
        var value = HostSettings.Default;
        HostSettingsPolicy.Validate(value);
        Assert.Equal(58731, value.Service.WebPort);
        Assert.Equal(58732, value.Service.McpPort);
        Assert.Equal(7, value.HistoryRetentionDays);
        Assert.True(value.Desktop.MinimizeToTray);
        Assert.Equal(DesktopMode.Desktop, value.Desktop.Mode);
        Assert.True(value.Updates.CheckEnabled);
        Assert.Equal(UpdateChannel.Prerelease, value.Updates.Channel);
        Assert.Equal(PluginSourceChannel.Stable, value.Updates.PluginSource);
        Assert.False(value.AutoStart);
        Assert.False(value.Service.McpEnabled);
        Assert.False(value.Service.McpWriteEnabled);
        Assert.False(value.Service.AllowRemoteAccess);
    }

    [Fact(DisplayName = "An Automatic Web Port Is Saved Without Changing The Current Listener Until Restart")]
    public void AcceptsAutomaticPort()
    {
        var before = HostSettings.Default;
        var values = before with { Service = before.Service with { WebPort = 0, McpEnabled = true } };
        var saved = HostSettingsPolicy.Next(new(0, before), 0, values);
        Assert.Equal(0, saved.Values.Service.WebPort);
        Assert.Equal(new[] { SettingsRestartReason.WebListener, SettingsRestartReason.Mcp },
            HostSettingsPolicy.Compare(before, saved.Values).RestartReasons);
    }

    [Theory(DisplayName = "Invalid Settings Cannot Create A New Revision Or Change The Prior Snapshot")]
    [InlineData("web-port")]
    [InlineData("mcp-port")]
    [InlineData("port-collision")]
    [InlineData("retention")]
    [InlineData("proxy-required")]
    [InlineData("embedded-credential")]
    [InlineData("unknown-choice")]
    [InlineData("duplicate-extension")]
    public void RejectsInvalidSettings(string fault)
    {
        var value = HostSettings.Default;
        var candidate = fault switch
        {
            "web-port" => value with { Service = value.Service with { WebPort = 80 } },
            "mcp-port" => value with { Service = value.Service with { McpPort = 70000 } },
            "port-collision" => value with { Service = value.Service with { McpEnabled = true, McpPort = value.Service.WebPort } },
            "retention" => value with { HistoryRetentionDays = 181 },
            "proxy-required" => value with { Proxy = value.Proxy with { Mode = ProxyMode.Http } },
            "embedded-credential" => value with { Proxy = value.Proxy with { Url = "http://fixture:fixture@proxy.invalid" } },
            "unknown-choice" => value with { LogLevel = (HostLogLevel)99 },
            _ => value with { Extensions = [new("fixture-extension", true), new("fixture-extension", false)] }
        };
        var before = new HostSettingsSnapshot(3, value);
        Assert.Throws<HostSettingsException>(() => HostSettingsPolicy.Next(before, 3, candidate));
        Assert.Equal(3, before.Revision);
        Assert.Same(value, before.Values);
    }

    [Fact(DisplayName = "Saved Settings Keep Restart Requirements Separate From Live And System Registration Changes")]
    public void SeparatesRestartRequirements()
    {
        var before = HostSettings.Default;
        var live = before with { HistoryRetentionDays = 30, Locale = HostLocale.EnUs };
        Assert.Empty(HostSettingsPolicy.Compare(before, live).RestartReasons);
        var startup = live with { AutoStart = true };
        Assert.True(HostSettingsPolicy.Compare(live, startup).StartupRegistrationChanged);
        var restart = startup with { Desktop = startup.Desktop with { Mode = DesktopMode.Browser },
            Service = startup.Service with { WebPort = 60000, McpEnabled = true }, Updates = startup.Updates with { PluginSource = PluginSourceChannel.Develop } };
        Assert.Equal(new[] { SettingsRestartReason.WebListener, SettingsRestartReason.DesktopMode, SettingsRestartReason.Mcp, SettingsRestartReason.PluginSource },
            HostSettingsPolicy.Compare(startup, restart).RestartReasons.ToArray());
        Assert.Equal(58731, before.Service.WebPort);
    }

    [Fact(DisplayName = "A Stale Settings Revision Cannot Overwrite The Latest Saved Choices")]
    public void RejectsStaleRevisions()
    {
        var before = new HostSettingsSnapshot(2, HostSettings.Default);
        Assert.Equal("settings_revision_conflict", Assert.Throws<HostSettingsException>(() =>
            HostSettingsPolicy.Next(before, 1, before.Values with { AutoStart = true })).Message);
    }
}
