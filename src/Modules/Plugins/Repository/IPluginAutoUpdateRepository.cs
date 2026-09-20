using NexusPipeline.ControlPlane.Http;
namespace NexusPipeline.Modules.Plugins.Repository;

/// <summary>Catalog and pending-operation boundary used by automatic plugin update coordination.</summary>
internal interface IPluginAutoUpdateRepository
{
    Task<IReadOnlyList<PluginStoreItem>> GetUpdateCandidatesAsync(CancellationToken cancellationToken);

    Task<PluginBatchUpdateResult> StageUpdatesAsync(
        IReadOnlyList<PluginStoreItem> candidates,
        CancellationToken cancellationToken);

    IReadOnlyList<PluginPendingOperation> ReadPendingOperations();
}
