using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.Plugins.Contracts;

/// <summary>Notification port kept on the Plugins side of the module boundary.</summary>
internal interface IPluginNotificationSink
{
    ValueTask<IReadOnlyList<PluginNotificationTask>> ListAsync(CancellationToken token);
    string? ValidateTargets(IReadOnlyList<string> ids, IReadOnlyList<string>? previous);
    ValueTask SendAsync(PluginNotification notification, CancellationToken cancellationToken = default);
}
