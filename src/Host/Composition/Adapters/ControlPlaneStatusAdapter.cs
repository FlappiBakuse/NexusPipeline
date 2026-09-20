using NexusPipeline.ControlPlane.Http;
using NexusPipeline.ControlPlane.Mcp;
using NexusPipeline.Modules.Diagnostics.Contracts;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>Host 组合层把具体控制面监听器投影成 Diagnostics 只读端口。</summary>
internal sealed class ControlPlaneStatusAdapter : IControlPlaneStatusReader
{
    public ListenerStatus? Web => WebServer.Current is { } server
        ? new ListenerStatus(server.IsRunning, server.Port)
        : null;

    public ListenerStatus? Mcp => McpHost.Current is { } host
        ? new ListenerStatus(host.IsRunning, host.Port)
        : null;
}
