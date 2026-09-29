namespace NexusPipeline.Modules.Plugins.Repository;

internal sealed record PluginInstallCompletion(
    string OperationId,
    string Name,
    string ArtifactName,
    string Kind,
    string Action,
    bool EnableAfterInstall,
    PluginOwnership InstalledIdentity);

internal enum PluginActivationResult
{
    Enabled,
    Preserved,
    NotApplicable,
    Failed,
}

internal interface IPluginActivationPreferences
{
    PluginActivationResult ApplyInstallIntent(PluginInstallCompletion completion);
}
