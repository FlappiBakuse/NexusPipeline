using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NexusPipeline.Mcp;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class McpProtocolTests
{
    [Fact]
    public async Task EmbeddedHttpHost_discovers_readonly_tools_and_returns_structured_result()
    {
        int port = GetFreePort();
        using var host = new McpHost(RuntimeContext.Instance, allowDestructiveTools: false, requestRestart: null);
        Assert.True(host.TryStart(port));
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            using (var rejected = new HttpRequestMessage(HttpMethod.Post, host.Endpoint))
            {
                rejected.Headers.Host = $"evil.example:{port}";
                rejected.Content = new StringContent("{}");
                using HttpResponseMessage rejectedResponse = await http.SendAsync(rejected);
                Assert.True(
                    rejectedResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
                    $"unexpected status: {(int)rejectedResponse.StatusCode}");
            }
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(host.Endpoint),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    EnableStandaloneGetStream = false,
                },
                http,
                NullLoggerFactory.Instance,
                ownsHttpClient: false);
            await using McpClient client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ProtocolVersion = "2025-11-25",
                    ClientInfo = new Implementation { Name = "NexusPipeline.Tests", Version = "1.0" },
                },
                NullLoggerFactory.Instance);

            IList<McpClientTool> tools = await client.ListToolsAsync();
            Assert.Contains(tools, tool => tool.Name == "get_status");
            Assert.Contains(tools, tool => tool.Name == "get_settings");
            Assert.Contains(tools, tool => tool.Name == "list_plugin_store");
            Assert.Contains(tools, tool => tool.Name == "refresh_plugin_store");
            Assert.Contains(tools, tool => tool.Name == "get_user_global_settings");
            Assert.Contains(tools, tool => tool.Name == "list_plugin_user_settings");
            Assert.Contains(tools, tool => tool.Name == "get_plugin_user_settings");
            Assert.DoesNotContain(tools, tool => tool.Name == "delete_script");
            Assert.DoesNotContain(tools, tool => tool.Name == "set_secret");
            Assert.DoesNotContain(tools, tool => tool.Name == "install_plugin");

            CallToolResult result = await client.CallToolAsync("get_settings");
            Assert.False(result.IsError);
            Assert.True(result.StructuredContent.HasValue);
            Assert.Contains("mcpEnabled", result.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public void McpSecurity_rejects_non_loopback_hosts_and_origins()
    {
        Assert.Equal(
            "MCP 仅接受 loopback Host",
            McpSecurity.Validate(new DefaultHttpContext
            {
                Request =
                {
                Host = new HostString("192.168.1.20", 58732),
                },
            }, 58732));

        var wrongOrigin = new DefaultHttpContext();
        wrongOrigin.Request.Host = new HostString("127.0.0.1", 58732);
        wrongOrigin.Request.Headers.Origin = "http://evil.example:58732";
        Assert.Equal(
            "MCP Origin 不在 loopback 允许列表中",
            McpSecurity.Validate(wrongOrigin, 58732));

        var oversized = new DefaultHttpContext();
        oversized.Request.Host = new HostString("localhost", 58732);
        oversized.Request.ContentLength = McpSecurity.MaxRequestBodyBytes + 1L;
        Assert.Equal(
            "MCP 请求体超过大小限制",
            McpSecurity.Validate(oversized, 58732));
    }

    [Fact]
    public async Task EmbeddedHttpHost_registers_destructive_tools_only_when_enabled()
    {
        int port = GetFreePort();
        using var host = new McpHost(RuntimeContext.Instance, allowDestructiveTools: true, requestRestart: null);
        Assert.True(host.TryStart(port));
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(host.Endpoint),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    EnableStandaloneGetStream = false,
                },
                http,
                NullLoggerFactory.Instance,
                ownsHttpClient: false);
            await using McpClient client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ProtocolVersion = "2025-11-25",
                    ClientInfo = new Implementation { Name = "NexusPipeline.Tests", Version = "1.0" },
                },
                NullLoggerFactory.Instance);

            IList<McpClientTool> tools = await client.ListToolsAsync();
            Assert.Contains(tools, tool => tool.Name == "delete_script");
            Assert.Contains(tools, tool => tool.Name == "set_secret");
            Assert.Contains(tools, tool => tool.Name == "enable_plugin");
            Assert.Contains(tools, tool => tool.Name == "disable_plugin");
            Assert.Contains(tools, tool => tool.Name == "install_plugin");
            Assert.Contains(tools, tool => tool.Name == "update_plugin");
            Assert.Contains(tools, tool => tool.Name == "uninstall_plugin");
            Assert.Contains(tools, tool => tool.Name == "trust_plugin_frontend");
            Assert.Contains(tools, tool => tool.Name == "revoke_plugin_frontend");
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public void EmbeddedHttpHost_does_not_drift_when_port_is_occupied()
    {
        int port = GetFreePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using var host = new McpHost(RuntimeContext.Instance, allowDestructiveTools: false, requestRestart: null);

        Assert.False(host.TryStart(port));
        Assert.False(host.IsRunning);
        Assert.Null(McpHost.Current);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
