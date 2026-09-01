using System.Net.Sockets;
using System.Text;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class JudgeScreenshotBridgeTests
{
    [Fact]
    public async Task BridgeRequiresInvocationTokenAndReturnsScreenshotId()
    {
        var screenshot = new RunScreenshot(
            "screenshot-0000000001",
            1,
            DateTimeOffset.Now,
            1,
            640,
            480,
            "pc",
            "judge-manual",
            new byte[] { 1 });
        await using var bridge = JudgeScreenshotBridge.Start(_ => Task.FromResult<RunScreenshot?>(screenshot));

        string rejected = await SendAsync(bridge.Endpoint, "invalid-token");
        Assert.Contains("401 Unauthorized", rejected, StringComparison.Ordinal);

        string accepted = await SendAsync(bridge.Endpoint, bridge.Token);
        Assert.Contains("200 OK", accepted, StringComparison.Ordinal);
        Assert.Contains("screenshot-0000000001", accepted, StringComparison.Ordinal);
    }

    private static async Task<string> SendAsync(string endpoint, string token)
    {
        var uri = new Uri(endpoint);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);
        using NetworkStream stream = client.GetStream();
        string request = $"POST {uri.AbsolutePath} HTTP/1.1\r\n"
            + $"Host: {uri.Host}:{uri.Port}\r\n"
            + $"X-Nexus-Screenshot-Token: {token}\r\n"
            + "Content-Length: 0\r\n"
            + "Connection: close\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
