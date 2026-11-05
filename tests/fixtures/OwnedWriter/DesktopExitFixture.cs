using System.Net.Http.Json;
using System.Text.Json;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;

internal static class DesktopExitFixture
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var configPath, var outputPath]) return 2;
        using var config = StrictJson.Parse(File.ReadAllBytes(configPath));
        using var http = new HttpClient { BaseAddress = new Uri(config.RootElement.GetProperty("address").GetString()!), Timeout = TimeSpan.FromSeconds(10) };
        var key = config.RootElement.GetProperty("verificationKey").GetString()!;
        var proofs = new DesktopProofReader(new ContractReader());
        var challenge = Guid.NewGuid();
        using var connected = await http.GetAsync("control/v1/desktop/session?correlationId=" + challenge);
        connected.EnsureSuccessStatusCode();
        using var session = proofs.Read(await connected.Content.ReadAsByteArrayAsync(), key, "desktop-session.schema.json");
        if (session.RootElement.GetProperty("correlationId").GetGuid() != challenge) throw new IOException("Unexpected handshake challenge");
        var correlation = Guid.NewGuid();
        var identity = session.RootElement;
        using var response = await http.PostAsJsonAsync("control/v1/desktop/authorize", new
        {
            format = "nexus.desktop.channel", version = 1, type = "close.request", correlationId = correlation,
            instanceId = identity.GetProperty("instanceId").GetGuid(), clientId = identity.GetProperty("clientId").GetGuid(),
            sessionId = identity.GetProperty("sessionId").GetGuid()
        });
        response.EnsureSuccessStatusCode();
        using var permit = proofs.Read(await response.Content.ReadAsByteArrayAsync(), key, "desktop-channel.schema.json");
        var approved = permit.RootElement;
        if (approved.GetProperty("type").GetString() != "close.permit" || !approved.GetProperty("isApproved").GetBoolean() ||
            approved.GetProperty("correlationId").GetGuid() != correlation || approved.GetProperty("expiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow ||
            new[] { "instanceId", "clientId", "sessionId" }.Any(name => approved.GetProperty(name).GetGuid() != identity.GetProperty(name).GetGuid()))
            throw new IOException("Unexpected close permit binding");
        var staging = outputPath + ".prepared";
        using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(JsonSerializer.SerializeToUtf8Bytes(new { scope = "Actual controlled native client, no UI", processId = Environment.ProcessId,
                closePermit = approved.Clone(), normalExit = true }));
            output.Flush(flushToDisk: true);
        }
        File.Move(staging, outputPath);
        return 0;
    }
}
