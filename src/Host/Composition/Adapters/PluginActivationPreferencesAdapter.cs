using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Persistence;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class PluginActivationPreferencesAdapter : IPluginActivationPreferences
{
    private readonly SettingsState _state;
    private readonly Action<AppSettings> _save;

    internal PluginActivationPreferencesAdapter(SettingsState state, Action<AppSettings>? save = null)
    {
        _state = state;
        _save = save ?? (settings => AppSettingsStore.Save(settings));
    }

    public PluginActivationResult ApplyInstallIntent(PluginInstallCompletion completion)
    {
        if (!completion.EnableAfterInstall || completion.Action != "install"
            || completion.Kind != "managed-code"
            || !Guid.TryParseExact(completion.OperationId, "N", out _)
            || !string.Equals(completion.Name, completion.InstalledIdentity.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(completion.ArtifactName, completion.InstalledIdentity.ArtifactName, StringComparison.Ordinal))
        {
            return PluginActivationResult.NotApplicable;
        }

        lock (_state.MutationLock)
        {
            AppSettings current = _state.Current;
            if (current.PluginPreferences?.Keys.Any(key =>
                string.Equals(key, completion.Name, StringComparison.OrdinalIgnoreCase)) == true)
            {
                return PluginActivationResult.Preserved;
            }
            AppSettings candidate = current.Clone();
            candidate.PluginPreferences ??= new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase);
            candidate.PluginPreferences[completion.Name] = new PluginPreference { Enabled = true };
            _save(candidate);
            _state.ReplaceAfterSave(candidate);
            return PluginActivationResult.Enabled;
        }
    }
}
