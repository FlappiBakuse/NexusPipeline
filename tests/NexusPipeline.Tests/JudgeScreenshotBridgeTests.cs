using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class JudgeRuntimeBridgeTests
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
        await using var bridge = JudgeRuntimeBridge.Start(_ => Task.FromResult<RunScreenshot?>(screenshot));

        string rejected = await SendAsync(bridge.ScreenshotEndpoint, "invalid-token", "X-Nexus-Screenshot-Token");
        Assert.Contains("401 Unauthorized", rejected, StringComparison.Ordinal);

        string accepted = await SendAsync(bridge.ScreenshotEndpoint, bridge.Token, "X-Nexus-Screenshot-Token");
        Assert.Contains("200 OK", accepted, StringComparison.Ordinal);
        Assert.Contains("screenshot-0000000001", accepted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeRoutesUseJudgeTokenAndReturnReadOnlyResults()
    {
        await using var bridge = JudgeRuntimeBridge.Start();

        string oldHeader = await SendAsync(
            bridge.Endpoint + "/processes",
            bridge.Token,
            "X-Nexus-Screenshot-Token",
            "{}");
        Assert.Contains("401 Unauthorized", oldHeader, StringComparison.Ordinal);

        string processes = await SendAsync(
            bridge.Endpoint + "/processes",
            bridge.Token,
            "X-Nexus-Judge-Token",
            "{\"nameContains\":\"dotnet\"}");
        Assert.Contains("200 OK", processes, StringComparison.Ordinal);
        Assert.Contains("\"processes\"", processes, StringComparison.Ordinal);
        Assert.Contains("\"truncated\"", processes, StringComparison.Ordinal);
        Assert.DoesNotContain("commandLine", processes, StringComparison.OrdinalIgnoreCase);

        string windows = await SendAsync(
            bridge.Endpoint + "/windows",
            bridge.Token,
            "X-Nexus-Judge-Token",
            "{}");
        Assert.Contains("200 OK", windows, StringComparison.Ordinal);
        Assert.Contains("\"windows\"", windows, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpProbeRouteKeepsInvalidUrlInsideJsonContract()
    {
        await using var bridge = JudgeRuntimeBridge.Start();

        string response = await SendAsync(
            bridge.Endpoint + "/http-probe",
            bridge.Token,
            "X-Nexus-Judge-Token",
            "{\"url\":\"file:///secret\"}");

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        string body = response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_url", document.RootElement.GetProperty("error").GetString());
    }

    private static async Task<string> SendAsync(
        string endpoint,
        string token,
        string headerName,
        string body = "")
    {
        var uri = new Uri(endpoint);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);
        using NetworkStream stream = client.GetStream();
        string request = $"POST {uri.AbsolutePath} HTTP/1.1\r\n"
            + $"Host: {uri.Host}:{uri.Port}\r\n"
            + $"{headerName}: {token}\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
            + "Connection: close\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        if (body.Length > 0)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
