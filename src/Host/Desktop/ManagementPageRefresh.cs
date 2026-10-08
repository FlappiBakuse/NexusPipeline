using System.Threading.Channels;
using NexusPipeline.ControlPlane.Http.Services;

namespace NexusPipeline.Host.Desktop;

internal sealed class ManagementPageRefresh : IManagementPageRefreshPort
{
    private readonly object _gate = new();
    private readonly HashSet<Channel<ManagementPageRefreshRequest>> _subscribers = [];

    public ManagementPageRefreshSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<ManagementPageRefreshRequest>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subscribers.Add(channel);
        return new(channel.Reader, () =>
        {
            lock (_gate) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
    }

    internal int Request()
    {
        var request = new ManagementPageRefreshRequest(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds());
        lock (_gate)
        {
            // Only connections present at this instant receive the request; there is no replay.
            return _subscribers.Count(channel => channel.Writer.TryWrite(request));
        }
    }
}
