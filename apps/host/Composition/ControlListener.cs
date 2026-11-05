using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace NexusPipeline.Host.Composition;

internal sealed class ControlListener : IDisposable
{
    private Socket? reserved;
    private readonly IPEndPoint endpoint;
    private readonly IPEndPoint? mcpEndpoint;

    public ControlListener(IPAddress address, int port, int? mcpPort)
    {
        if (port is < 0 or > 65535 || port != 0 && port == mcpPort)
            throw new ArgumentException("control_listener_ports_conflict");
        mcpEndpoint = mcpPort is { } value ? new(IPAddress.Loopback, value) : null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(address, port));
                var bound = (IPEndPoint)socket.LocalEndPoint!;
                if (bound.Port == mcpPort) { socket.Dispose(); continue; }
                endpoint = bound;
                reserved = socket;
                return;
            }
            catch { socket.Dispose(); throw; }
        }
        throw new IOException("control_listener_port_unavailable");
    }

    public int Port => endpoint.Port;

    public Socket CreateSocket(EndPoint requested)
    {
        if (endpoint.Equals(requested))
            return Interlocked.Exchange(ref reserved, null) ?? throw new InvalidOperationException("control_listener_already_claimed");
        if (mcpEndpoint?.Equals(requested) == true)
            return SocketTransportOptions.CreateDefaultBoundListenSocket(requested);
        throw new InvalidOperationException("control_listener_endpoint_unbound");
    }

    public void Dispose() => Interlocked.Exchange(ref reserved, null)?.Dispose();
}
