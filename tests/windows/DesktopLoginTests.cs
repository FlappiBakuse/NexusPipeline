using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusPipeline.Platform.Security;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class DesktopLoginTests
{
    [Fact(DisplayName = "Actual Native Socket Owner Receives Signed Login Polls While Stale Session And Unbound Cleanup Claims Are Rejected")]
    public async Task BindsCaptureProtocol()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "desktop-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var host = await DesktopControlTests.HostRun.StartAsync(Path.Combine(root, "runtime"), Environment.ProcessId);
        using var http = new HttpClient { BaseAddress = host.Address };
        using var sessionResponse = await http.GetAsync("control/v1/desktop/session?correlationId=" + Guid.NewGuid().ToString("D"));
        sessionResponse.EnsureSuccessStatusCode();
        using var sessionProof = new DesktopProofReader(new()).Read(await sessionResponse.Content.ReadAsByteArrayAsync(), host.VerificationKey!, "desktop-session.schema.json");
        var session = sessionProof.RootElement;
        var request = new Dictionary<string, object>
        {
            ["format"] = "nexus.desktop.login", ["version"] = 1, ["type"] = "poll",
            ["instanceId"] = session.GetProperty("instanceId").GetString()!, ["nativeClientId"] = session.GetProperty("clientId").GetString()!,
            ["sessionId"] = session.GetProperty("sessionId").GetString()!, ["correlationId"] = Guid.NewGuid().ToString("D")
        };
        using var response = await http.PostAsJsonAsync("control/v1/desktop/login", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signed = await response.Content.ReadAsByteArrayAsync();
        using var proof = new DesktopProofReader(new()).Read(signed, host.VerificationKey!, "desktop-login.schema.json");
        Assert.Equal("offers", proof.RootElement.GetProperty("type").GetString());
        Assert.Empty(proof.RootElement.GetProperty("offers").EnumerateArray());
        Assert.Equal((string)request["correlationId"], proof.RootElement.GetProperty("correlationId").GetString());
        File.WriteAllBytes(Path.Combine(root, "signed-login-poll.json"), signed);
        var stale = new Dictionary<string, object>(request) { ["sessionId"] = Guid.NewGuid().ToString("D") };
        using (var rejected = await http.PostAsJsonAsync("control/v1/desktop/login", stale)) Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        var malformed = new Dictionary<string, object>(request) { ["values"] = new { injected = "synthetic" } };
        using (var rejected = await http.PostAsJsonAsync("control/v1/desktop/login", malformed)) Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var forged = new Dictionary<string, object>(request) { ["type"] = "complete", ["captureId"] = Guid.NewGuid().ToString("D"),
            ["nonce"] = Guid.NewGuid().ToString("D"), ["status"] = "canceled", ["values"] = new { } };
        using (var rejected = await http.PostAsJsonAsync("control/v1/desktop/login", forged)) Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
    }
}
