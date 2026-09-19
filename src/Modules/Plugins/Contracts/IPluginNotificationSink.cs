using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.Plugins.Contracts;

/// <summary>Notification port kept on the Plugins side of the module boundary.</summary>
internal interface IPluginNotificationSink
{
    ValueTask SendAsync(PluginNotification notification, CancellationToken cancellationToken = default);
}
