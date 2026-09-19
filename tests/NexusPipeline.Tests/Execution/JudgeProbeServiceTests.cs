using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Tests.Execution;

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
    public void WindowProjectionFiltersBoundsAndTruncatesTitles()
    {
        var nodes = new Dictionary<int, ProcessTree.ProcessNode>
        {
            [5] = new ProcessTree.ProcessNode(5, 1, "game-helper.exe"),
            [12] = new ProcessTree.ProcessNode(12, 1, "Game.exe"),
        };
        string longTitle = "Game " + new string('x', 300);
        var windows = new[]
        {
            new ProcessWindows.VisibleTopLevelWindow(new IntPtr(120), 12, longTitle, false),
            new ProcessWindows.VisibleTopLevelWindow(new IntPtr(50), 5, "game helper", true),
            new ProcessWindows.VisibleTopLevelWindow(new IntPtr(80), 19, "browser", false),
        };

        JudgeWindowResponse response = JudgeProbeService.ProjectWindows(
            windows,
            nodes,
            titleContains: "GAME",
            maxResults: 1);

        Assert.True(response.Truncated);
        Assert.Single(response.Windows);
        Assert.Equal(5, response.Windows[0].Pid);
        Assert.Equal("game-helper.exe", response.Windows[0].Name);
        Assert.Equal("game helper", response.Windows[0].Title);
        Assert.True(response.Windows[0].Foreground);

        JudgeWindowResponse unbounded = JudgeProbeService.ProjectWindows(windows, nodes, maxResults: 10);
        Assert.Equal(3, unbounded.Windows.Count);
        Assert.Equal(256, unbounded.Windows[1].Title.Length);
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
            var provider = new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct);

            string json = await JudgeHttpProbe.RunAsync(
                $"http://127.0.0.1:{port}/probe",
                "{\"timeoutMs\":5000,\"maxBytes\":4}",
                provider,
                CancellationToken.None);

            await server;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean(), json);
            Assert.Equal(200, root.GetProperty("status").GetInt32());
            Assert.Equal("0123", root.GetProperty("body").GetString());
            Assert.True(root.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HttpProbeReturnsNonSuccessStatusAndBody()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = ServeOnceAsync(
                listener,
                "HTTP/1.1 403 Forbidden\r\nContent-Length: 9\r\nConnection: close\r\n\r\nforbidden"u8.ToArray());
            var provider = new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct);

            string json = await JudgeHttpProbe.RunAsync(
                $"http://127.0.0.1:{port}/forbidden",
                "{\"timeoutMs\":5000}",
                provider,
                CancellationToken.None);

            await server;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal(403, root.GetProperty("status").GetInt32());
            Assert.Equal("forbidden", root.GetProperty("body").GetString());
            Assert.False(root.TryGetProperty("error", out _));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HttpProbeTimesOutWhenServerDoesNotRespond()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = DelayOnceAsync(listener, TimeSpan.FromMilliseconds(500));
            var provider = new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct);

            string json = await JudgeHttpProbe.RunAsync(
                $"http://127.0.0.1:{port}/slow",
                "{\"timeoutMs\":25}",
                provider,
                CancellationToken.None);

            await server;
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal("timeout", document.RootElement.GetProperty("error").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("status").GetInt32());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HttpProbeFollowsRedirects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = ServeResponsesAsync(
                listener,
                "HTTP/1.1 302 Found\r\nLocation: /final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nfinal"u8.ToArray());
            var provider = new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct);

            string json = await JudgeHttpProbe.RunAsync(
                $"http://127.0.0.1:{port}/start",
                "{\"timeoutMs\":5000}",
                provider,
                CancellationToken.None);

            await server;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean(), json);
            Assert.Equal(200, root.GetProperty("status").GetInt32());
            Assert.Equal("final", root.GetProperty("body").GetString());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    public async Task HttpProbeDecodesCompressedResponses(string encoding)
    {
        const string body = "compressed response";
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (Stream compressor = encoding == "gzip"
                ? new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)
                : new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                await compressor.WriteAsync(bytes);
            }
            compressed = output.ToArray();
        }
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Encoding: {encoding}\r\nContent-Length: {compressed.Length}\r\nConnection: close\r\n\r\n");
        byte[] response = new byte[header.Length + compressed.Length];
        Buffer.BlockCopy(header, 0, response, 0, header.Length);
        Buffer.BlockCopy(compressed, 0, response, header.Length, compressed.Length);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = ServeOnceAsync(listener, response);
            var provider = new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct);

            string json = await JudgeHttpProbe.RunAsync(
                $"http://127.0.0.1:{port}/compressed",
                "{\"timeoutMs\":5000}",
                provider,
                CancellationToken.None);

            await server;
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), json);
            Assert.Equal(body, document.RootElement.GetProperty("body").GetString());
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeOnceAsync(TcpListener listener, byte[]? response = null)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = client.GetStream();
        await DrainRequestHeadersAsync(stream);
        response ??= "HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\n0123456789"u8.ToArray();
        await stream.WriteAsync(response);
    }

    private static async Task ServeResponsesAsync(TcpListener listener, params byte[][] responses)
    {
        foreach (byte[] response in responses)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = client.GetStream();
            await DrainRequestHeadersAsync(stream);
            await stream.WriteAsync(response);
        }
    }

    private static async Task DelayOnceAsync(TcpListener listener, TimeSpan delay)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        await DrainRequestHeadersAsync(client.GetStream());
        await Task.Delay(delay);
    }

    private static async Task DrainRequestHeadersAsync(NetworkStream stream)
    {
        using var received = new MemoryStream();
        byte[] buffer = new byte[1024];
        while (received.Length < 8192)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return;
            }
            received.Write(buffer, 0, read);
            if (received.Length >= 4)
            {
                byte[] bytes = received.GetBuffer();
                int length = checked((int)received.Length);
                if (bytes[length - 4] == '\r'
                    && bytes[length - 3] == '\n'
                    && bytes[length - 2] == '\r'
                    && bytes[length - 1] == '\n')
                {
                    return;
                }
            }
        }
    }
}
