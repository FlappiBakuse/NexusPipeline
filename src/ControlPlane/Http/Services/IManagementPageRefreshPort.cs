using System.Threading.Channels;

namespace NexusPipeline.ControlPlane.Http.Services;

internal sealed record ManagementPageRefreshRequest(string RequestId, long ExpiresAt);

internal interface IManagementPageRefreshPort
{
    ManagementPageRefreshSubscription Subscribe();
}

internal sealed class ManagementPageRefreshSubscription(ChannelReader<ManagementPageRefreshRequest> reader, Action dispose) : IDisposable
{
    internal ValueTask<ManagementPageRefreshRequest> ReadAsync(CancellationToken token) => reader.ReadAsync(token);
    public void Dispose() => dispose();
}
