namespace NexusPipeline.Plugins;

internal sealed partial class PluginRepositoryService
{
    public Task<PluginPendingOperation> InstallAsync(
        string name,
        bool update,
        CancellationToken cancellationToken = default) =>
        _operations.InstallAsync(name, update, cancellationToken);

    public Task<PluginPendingOperation> UninstallAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        _operations.UninstallAsync(name, cancellationToken);
}
