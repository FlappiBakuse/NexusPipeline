using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using NexusPipeline.Core.Settings;

namespace NexusPipeline.Host.Composition;

internal sealed class ControlListener : IDisposable
{
    private Socket? reserved;
    private Socket? reservedMcp;
    private readonly IPEndPoint endpoint;
    private readonly IPEndPoint? mcpEndpoint;

    public ControlListener(IPAddress address, int port, int? mcpPort, RestartReservation? reservation = null)
    {
        if (port is < 0 or > 65535 || port != 0 && port == mcpPort)
            throw new ArgumentException("control_listener_ports_conflict");
        mcpEndpoint = mcpPort is { } value ? new(IPAddress.Loopback, value) : null;
        // Reserve the fixed endpoint first so the OS cannot give it to an automatic Web listener.
        if (mcpEndpoint is not null) reservedMcp = reservation?.TakeMcp() ?? Bind(mcpEndpoint, "mcp");
        try
        {
            reserved = reservation?.TakeWeb() ?? Bind(new IPEndPoint(address, port), "web");
            endpoint = (IPEndPoint)reserved.LocalEndPoint!;
        }
        catch { Dispose(); throw; }
    }

    public int Port => endpoint.Port;

    internal static RestartReservation PrepareRestart(ServicePreferences saved, int currentWeb, int? currentMcp, bool currentRemote)
    {
        var address = saved.AllowRemoteAccess ? IPAddress.Any : IPAddress.Loopback;
        int? mcp = saved.McpEnabled ? saved.McpPort : null;
        var web = saved.WebPort == 0 && currentWeb != mcp ? currentWeb : saved.WebPort;
        var reservedMcp = mcp is { } value && value != currentWeb && value != currentMcp
            ? Bind(new IPEndPoint(IPAddress.Loopback, value), "mcp") : null;
        try
        {
            if (saved.AllowRemoteAccess && (web == currentWeb && !currentRemote || web == currentMcp))
                CheckRemoteInterfaces(web);
            var reservedWeb = web != currentWeb && web != currentMcp ? Bind(new IPEndPoint(address, web), "web") : null;
            return new RestartReservation(reservedWeb is null ? web : ((IPEndPoint)reservedWeb.LocalEndPoint!).Port,
                reservedWeb, reservedMcp);
        }
        catch { reservedMcp?.Dispose(); throw; }
    }

    private static void CheckRemoteInterfaces(int port)
    {
        foreach (var address in NetworkInterface.GetAllNetworkInterfaces().SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address).Where(item => item.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item)).Distinct())
            using (Bind(new IPEndPoint(address, port), "web")) { }
    }

    internal sealed class RestartReservation(int port, Socket? web, Socket? mcp) : IDisposable
    {
        internal int Port { get; } = port;
        internal Socket? TakeWeb() => Interlocked.Exchange(ref web, null);
        internal Socket? TakeMcp() => Interlocked.Exchange(ref mcp, null);
        public void Dispose() { TakeWeb()?.Dispose(); TakeMcp()?.Dispose(); }
    }

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
