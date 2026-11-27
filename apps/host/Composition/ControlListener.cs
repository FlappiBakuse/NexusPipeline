using System.ComponentModel;
using System.Net;
using System.Net.Sockets;

namespace NexusPipeline.Host.Composition;

internal sealed class ControlListener : IDisposable
{
    private Socket? reserved;
    private Socket? reservedMcp;
    private readonly IPEndPoint endpoint;
    private readonly IPEndPoint? mcpEndpoint;

    public ControlListener(IPAddress address, int port, int? mcpPort)
    {
        if (port is < 0 or > 65535 || port != 0 && port == mcpPort)
            throw new ArgumentException("control_listener_ports_conflict");
        mcpEndpoint = mcpPort is { } value ? new(IPAddress.Loopback, value) : null;
        // Reserve the fixed endpoint first so the OS cannot give it to an automatic Web listener.
        if (mcpEndpoint is not null) reservedMcp = Bind(mcpEndpoint, "mcp");
        try
        {
            reserved = Bind(new IPEndPoint(address, port), "web");
            endpoint = (IPEndPoint)reserved.LocalEndPoint!;
        }
        catch { Dispose(); throw; }
    }

    public int Port => endpoint.Port;

    public Socket CreateSocket(EndPoint requested)
    {
        if (endpoint.Equals(requested))
            return Interlocked.Exchange(ref reserved, null) ?? throw new InvalidOperationException("control_listener_already_claimed");
        if (mcpEndpoint?.Equals(requested) == true)
            return Interlocked.Exchange(ref reservedMcp, null) ?? throw new InvalidOperationException("control_listener_already_claimed");
        throw new InvalidOperationException("control_listener_endpoint_unbound");
    }

    private static Socket Bind(IPEndPoint endpoint, string listener)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(endpoint);
            return socket;
        }
        catch (SocketException error)
        {
            socket.Dispose();
            throw new Win32Exception(error.NativeErrorCode,
                FormattableString.Invariant($"control_{listener}_listener_port_{endpoint.Port}_bind_failed"));
        }
        catch { socket.Dispose(); throw; }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref reserved, null)?.Dispose();
        Interlocked.Exchange(ref reservedMcp, null)?.Dispose();
    }
}
