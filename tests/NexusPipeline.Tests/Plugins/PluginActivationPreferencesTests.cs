using NexusPipeline.Host.Composition.Adapters;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Settings;
using Xunit;

namespace NexusPipeline.Tests.Plugins;

public sealed class PluginActivationPreferencesTests
{
    [Fact]
    public void FirstInstallSavesEnabledAndReplayPreservesLaterDisable()
    {
        var state = new SettingsState(new AppSettings());
        int saves = 0;
        var adapter = new PluginActivationPreferencesAdapter(state, _ => saves++);
        PluginInstallCompletion completion = NewCompletion();

        Assert.Equal(PluginActivationResult.Enabled, adapter.ApplyInstallIntent(completion));
        Assert.True(state.Current.PluginPreferences[completion.Name].Enabled);
        Assert.Equal(1, saves);

        state.Current.PluginPreferences[completion.Name].Enabled = false;
        Assert.Equal(PluginActivationResult.Preserved, adapter.ApplyInstallIntent(completion));
        Assert.False(state.Current.PluginPreferences[completion.Name].Enabled);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void ExistingCaseInsensitivePreferenceAndFailedSaveLeaveStateUnchanged()
    {
        var settings = new AppSettings();
        settings.PluginPreferences["BETTERGI"] = new PluginPreference { Enabled = false };
        var state = new SettingsState(settings);
        var adapter = new PluginActivationPreferencesAdapter(state, _ => throw new IOException("disk full"));
        Assert.Equal(PluginActivationResult.Preserved, adapter.ApplyInstallIntent(NewCompletion()));
        Assert.False(state.Current.PluginPreferences["BETTERGI"].Enabled);

        state = new SettingsState(new AppSettings());
        adapter = new PluginActivationPreferencesAdapter(state, _ => throw new IOException("disk full"));
        AppSettings original = state.Current;
        Assert.Throws<IOException>(() => adapter.ApplyInstallIntent(NewCompletion()));
        Assert.Same(original, state.Current);
        Assert.Empty(state.Current.PluginPreferences);
    }

    private static PluginInstallCompletion NewCompletion()
    {
        var owner = new PluginOwnership
        {
            Name = "bettergi",
            ArtifactName = "BetterGI",
            Version = "1.0.0",
            Kind = "managed-code",
        };
        return new PluginInstallCompletion(Guid.NewGuid().ToString("N"), owner.Name,
            owner.ArtifactName, owner.Kind, "install", true, owner);
    }
}
