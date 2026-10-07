using NexusPipeline.Host.Composition.Adapters;
using NexusPipeline.Modules.Settings;
using Xunit;

namespace NexusPipeline.Tests.Host;

public sealed class SettingsChangedEffectsTests
{
    [Fact]
    public void SecretEditsDoNotRepeatWindowsSettingsButRelevantChangesDo()
    {
        var previous = new AppSettings { AllowRemoteAccess = true, AutoStart = true, WebPort = 58731 };
        var current = previous.Clone();
        current.AccessToken = "enc:owned-test";
        var ports = new List<int>();
        var startup = new List<bool>();
        SettingsChangedEffects.ApplyWindowsChanges(previous, current, ports.Add, startup.Add);
        Assert.Empty(ports);
        Assert.Empty(startup);
        current.WebPort = 58733;
        SettingsChangedEffects.ApplyWindowsChanges(previous, current, ports.Add, startup.Add);
        Assert.Equal(new[] { 58733 }, ports);
        Assert.Empty(startup);
        previous = current.Clone();
        current.AutoStart = false;
        SettingsChangedEffects.ApplyWindowsChanges(previous, current, ports.Add, startup.Add);
        Assert.Equal(new[] { false }, startup);
        previous = current.Clone();
        previous.AllowRemoteAccess = false;
        SettingsChangedEffects.ApplyWindowsChanges(previous, current, ports.Add, startup.Add);
        Assert.Equal(new[] { 58733, 58733 }, ports);
        previous = current.Clone();
        current.LightweightMode = true;
        SettingsChangedEffects.ApplyWindowsChanges(previous, current, ports.Add, startup.Add);
        Assert.Equal(2, ports.Count);
    }
}
