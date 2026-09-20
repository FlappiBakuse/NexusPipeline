using Xunit;
using NexusPipeline.Modules.Settings;
namespace NexusPipeline.Tests.Settings;


public sealed class AppSettingsCloneTests
{

    [Fact]
    public void SettingsClone_IsDetachedFromCurrentObject()
    {
        var settings = new AppSettings
        {
            WebPort = 12345,
            PluginRepository = new PluginRepositorySettings { Channel = "develop" },
            PluginPreferences = new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["notify"] = new PluginPreference { Enabled = true },
            },
        };

        AppSettings clone = settings.Clone();
        clone.WebPort = 23456;
        clone.PluginRepository.Channel = "stable";
        clone.PluginPreferences["demo"] = new PluginPreference { Enabled = true };

        Assert.Equal(12345, settings.WebPort);
        Assert.Single(settings.PluginPreferences);
        Assert.True(settings.PluginPreferences["notify"].Enabled);
        Assert.Equal("develop", settings.PluginRepository.Channel);
        Assert.Equal(23456, clone.WebPort);
        Assert.Equal("stable", clone.PluginRepository.Channel);
        Assert.Equal(2, clone.PluginPreferences.Count);
    }
}
