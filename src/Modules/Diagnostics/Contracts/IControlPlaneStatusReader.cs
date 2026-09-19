namespace NexusPipeline.Modules.Diagnostics.Contracts;

/// <summary>控制面监听状态的只读端口；Diagnostics 不依赖具体 HTTP/MCP 实现。</summary>
internal sealed record ListenerStatus(bool IsRunning, int Port);

internal interface IControlPlaneStatusReader
{
    ListenerStatus? Web { get; }

    ListenerStatus? Mcp { get; }
}
