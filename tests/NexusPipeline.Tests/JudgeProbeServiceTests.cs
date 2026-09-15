using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Networking;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class JudgeProbeServiceTests
{
    [Fact]
    public void ProcessProjectionFiltersCaseInsensitivelyAndBoundsResults()
    {
        var nodes = new Dictionary<int, ProcessTree.ProcessNode>
        {
            [12] = new ProcessTree.ProcessNode(12, 1, "Game.exe"),
            [5] = new ProcessTree.ProcessNode(5, 1, "game-helper.exe"),
            [19] = new ProcessTree.ProcessNode(19, 1, "browser.exe"),
        };
        DateTime start = new(2026, 9, 15, 10, 20, 30, DateTimeKind.Utc);

        JudgeProcessResponse response = JudgeProbeService.ProjectProcesses(
            nodes,
            nameContains: "GAME",
            identityProvider: pid => new ProcessIdentity(pid, start, "process.exe"),
            maxResults: 1);

        Assert.True(response.Truncated);
        Assert.Single(response.Processes);
        Assert.Equal(5, response.Processes[0].Pid);
        Assert.Equal(1, response.Processes[0].Ppid);
        Assert.Equal("game-helper.exe", response.Processes[0].Name);
        Assert.Equal(start, response.Processes[0].StartTimeUtc);
    }

    [Fact]
    public void ProcessProjectionHasOnlySafeIdentityFields()
    {
        var nodes = new Dictionary<int, ProcessTree.ProcessNode>
        {
            [7] = new ProcessTree.ProcessNode(7, 3, "sample.exe"),
        };

        string json = JudgeProbeService.Serialize(JudgeProbeService.ProjectProcesses(nodes, maxResults: 10));

        Assert.Contains("\"pid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ppid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HttpProbeRejectsNonHttpUrlsAndBoundsUrlLength()
    {
        Assert.False(JudgeHttpProbe.TryCreateUri("file:///secret", out _));
        Assert.False(JudgeHttpProbe.TryCreateUri("http://", out _));
        Assert.False(JudgeHttpProbe.TryCreateUri(new string('a', JudgeHttpProbe.MaxUrlLength + 1), out _));
        Assert.True(JudgeHttpProbe.TryCreateUri("https://example.com/path", out Uri? uri));
        Assert.Equal("https", uri!.Scheme);
    }

    [Fact]
    public async Task HttpProbeUsesLoopbackProviderAndTruncatesBody()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = ServeOnceAsync(listener);
            var provider = new OutboundHttpClientProvider(() => new AppSettings());

            string json = await JudgeHttpProbe.RunAsync(
                $"http://127.0.0.1:{port}/probe",
                "{\"timeoutMs\":5000,\"maxBytes\":4}",
                provider,
                CancellationToken.None);

            await server;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal(200, root.GetProperty("status").GetInt32());
            Assert.Equal("0123", root.GetProperty("body").GetString());
            Assert.True(root.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeOnceAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = client.GetStream();
        byte[] response = "HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\n0123456789"u8.ToArray();
        await stream.WriteAsync(response);
    }
}
