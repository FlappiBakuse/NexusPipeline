using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>把宿主通知实现绑定到 Plugins 的显式通知端口。</summary>
internal sealed class PluginNotificationSinkAdapter : IPluginNotificationSink
{
    private readonly NotificationDispatcher _dispatcher;

    public PluginNotificationSinkAdapter(NotificationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public ValueTask SendAsync(PluginNotification notification, CancellationToken cancellationToken = default)
    {
        return _dispatcher.SendPluginAsync(notification, cancellationToken);
    }
}
