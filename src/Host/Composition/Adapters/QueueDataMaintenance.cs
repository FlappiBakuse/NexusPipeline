using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Queues.Contracts;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class QueueDataMaintenance : IQueueDataMaintenance
{
    private readonly PluginManager _plugins;

    public QueueDataMaintenance(PluginManager plugins)
    {
        _plugins = plugins;
    }

    public void RemoveQueueData(string queueId) => _plugins.DeleteQueueData(queueId);
}
