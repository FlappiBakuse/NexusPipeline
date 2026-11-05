using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using NexusPipeline.Host.Composition;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ControlListenerTests
{
    [Theory(DisplayName = "The Automatic Control Port Remains Reserved Until Kestrel Owns The Same Socket")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservesAutomaticPort(bool mcpEnabled)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var mcpPort = mcpEnabled ? ((IPEndPoint)probe.LocalEndPoint!).Port : (int?)null;
        using var listener = new ControlListener(IPAddress.Loopback, 0, mcpPort);
        Assert.InRange(listener.Port, 1, 65535);
        Assert.NotEqual(mcpPort, listener.Port);
        using var competing = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.Throws<SocketException>(() => competing.Bind(new IPEndPoint(IPAddress.Loopback, listener.Port)));
        probe.Dispose();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseSockets(options => options.CreateBoundListenSocket = listener.CreateSocket);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, listener.Port);
            if (mcpPort is { } port) options.Listen(IPAddress.Loopback, port);
        });
        await using var app = builder.Build();
        app.MapGet("/", (Microsoft.AspNetCore.Http.HttpContext context) => context.Connection.LocalPort);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Assert.Equal(listener.Port.ToString(), await client.GetStringAsync($"http://127.0.0.1:{listener.Port}/"));
            if (mcpPort is { } port)
                Assert.Equal(port.ToString(), await client.GetStringAsync($"http://127.0.0.1:{port}/"));
            Assert.Throws<InvalidOperationException>(() => listener.CreateSocket(new IPEndPoint(IPAddress.Loopback, listener.Port)));
        }
        finally { await app.StopAsync(); }
    }
}
