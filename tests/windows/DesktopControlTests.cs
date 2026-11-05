using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class DesktopControlTests
{
    [Fact(DisplayName = "The Actual Native Tcp Peer Can Bind Only Its Exact Authenticated Frontend Grant And Receives A Signed Session Proof")]
    public async Task BindsFrontendGrant()
    {
        var root = CreateRoot(); using var host = await HostRun.StartAsync(Path.Combine(root, "runtime"), Environment.ProcessId);
        using var http = new HttpClient { BaseAddress = host.Address }; var session = await ReadSessionAsync(http, host.VerificationKey!);
        using var created = await http.PostAsync("control/v1/extensions/clients", null); created.EnsureSuccessStatusCode();
        using var grant = JsonDocument.Parse(await created.Content.ReadAsByteArrayAsync());
        var client = grant.RootElement.GetProperty("clientId").GetGuid(); var token = grant.RootElement.GetProperty("token").GetString()!;
        var request = new Dictionary<string, object> { ["format"] = "nexus.desktop.frontend", ["version"] = 1,
            ["instanceId"] = session.GetProperty("instanceId").GetString()!, ["nativeClientId"] = session.GetProperty("clientId").GetString()!,
            ["sessionId"] = session.GetProperty("sessionId").GetString()!, ["correlationId"] = Guid.NewGuid().ToString("D"), ["frontendClientId"] = client.ToString("D") };
        using (var missing = await http.PostAsJsonAsync("control/v1/desktop/frontend/bind", request)) Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        http.DefaultRequestHeaders.Add("X-Nexus-Frontend-Client", token);
        var mismatch = new Dictionary<string, object>(request) { ["frontendClientId"] = Guid.NewGuid().ToString("D") };
        using (var wrong = await http.PostAsJsonAsync("control/v1/desktop/frontend/bind", mismatch)) Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        var wrongSession = new Dictionary<string, object>(request) { ["sessionId"] = Guid.NewGuid().ToString("D") };
        using (var wrong = await http.PostAsJsonAsync("control/v1/desktop/frontend/bind", wrongSession)) Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        using var accepted = await http.PostAsJsonAsync("control/v1/desktop/frontend/bind", request); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var bytes = await accepted.Content.ReadAsByteArrayAsync();
        using var proof = new DesktopProofReader(new ContractReader()).Read(bytes, host.VerificationKey!, "desktop-frontend.schema.json");
        Assert.Equal(client, proof.RootElement.GetProperty("frontendClientId").GetGuid());
        Assert.Equal((string)request["correlationId"], proof.RootElement.GetProperty("correlationId").GetString());
        File.WriteAllBytes(Path.Combine(root, "signed-frontend.json"), bytes);
        File.WriteAllBytes(Path.Combine(root, "frontend-request.json"), JsonSerializer.SerializeToUtf8Bytes(request));
        File.WriteAllText(Path.Combine(root, "frontend-verification-key.txt"), host.VerificationKey!);
        using (var retired = await http.PostAsync("control/v1/extensions/clients/retire", null)) Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);
        using (var denied = await http.PostAsJsonAsync("control/v1/desktop/frontend/bind", request)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        http.DefaultRequestHeaders.Remove("X-Nexus-Frontend-Client");
        using var nativeRetire = await http.PostAsJsonAsync("control/v1/desktop/frontend/retire", request); Assert.Equal(HttpStatusCode.OK, nativeRetire.StatusCode);
    }

    [Fact(DisplayName = "A Verified Close Permit Freezes Native Actions Without Stopping The Still Running Client")]
    public async Task FreezesNativeActions()
    {
        var root = CreateRoot();
        using var host = await HostRun.StartAsync(Path.Combine(root, "runtime"), Environment.ProcessId);
        using var http = new HttpClient { BaseAddress = host.Address };
        var session = await ReadSessionAsync(http, host.VerificationKey!);
        var request = Action(session, "close.request");
        using var response = await http.PostAsJsonAsync("control/v1/desktop/authorize", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        File.WriteAllBytes(Path.Combine(root, "close-permit-response.json"), responseBytes);
        File.WriteAllText(Path.Combine(root, "close-verification-key.txt"), host.VerificationKey!);
        File.WriteAllBytes(Path.Combine(root, "close-request.json"), JsonSerializer.SerializeToUtf8Bytes(request));
        using var proof = new DesktopProofReader(new ContractReader()).Read(responseBytes,
            host.VerificationKey!, "desktop-channel.schema.json");
        Assert.Equal("close.permit", proof.RootElement.GetProperty("type").GetString());
        Assert.Equal((string)request["correlationId"], proof.RootElement.GetProperty("correlationId").GetString());
        Assert.NotEqual(Guid.Empty, proof.RootElement.GetProperty("permitId").GetGuid());
        Assert.True(proof.RootElement.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        using var blocked = await http.PostAsJsonAsync("control/v1/desktop/authorize", Action(session, "file.select"));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using var stillReady = await http.GetAsync("control/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, stillReady.StatusCode);
    }

    [Fact(DisplayName = "Client Exit After A Verified Close Permit Stops Host And Releases Its Port And Instance Lease")]
    public async Task ReleasesExitedClient()
    {
        var root = CreateRoot();
        var runtime = Path.Combine(root, "runtime");
        var config = Path.Combine(root, "client.json");
        var output = Path.Combine(root, "client-exit.json");
        HostRun? host = null;
        using var job = new OwnedWorkerJob();
        try
        {
            job.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!, ["desktop-exit", config, output], root,
                Guid.NewGuid(), Guid.NewGuid(), identity =>
                {
                    using (var recorded = new FileStream(Path.Combine(root, "client-identity.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        recorded.Write(JsonSerializer.SerializeToUtf8Bytes(identity));
                        recorded.Flush(flushToDisk: true);
                    }
                    host = HostRun.StartAsync(runtime, identity.ProcessId).GetAwaiter().GetResult();
                    using var input = new FileStream(config, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    input.Write(JsonSerializer.SerializeToUtf8Bytes(new { address = host.Address.AbsoluteUri, verificationKey = host.VerificationKey }));
                    input.Flush(flushToDisk: true);
                });
            Assert.NotNull(host);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(output)) await Task.Delay(20, deadline.Token);
            using var result = JsonDocument.Parse(File.ReadAllBytes(output));
            Assert.True(result.RootElement.GetProperty("normalExit").GetBoolean());
            await host.AwaitExitAsync(deadline.Token);
            Assert.Equal(0, host.ExitCode);
            using var connection = new System.Net.Sockets.TcpClient();
            await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => connection.ConnectAsync(IPAddress.Loopback, host.Address.Port));
            var instance = new InstanceRoot(new ContractReader()).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, new ContractReader());
            Console.WriteLine(JsonSerializer.Serialize(new { scope = "Real client exit and graceful Host stop; not full desktop-family exit", root, host.Identity }));
        }
        finally
        {
            host?.Dispose();
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact(DisplayName = "An Unavailable Native Owner Is Rejected Before Any Runtime Bytes Are Created")]
    public async Task RejectsUnavailableOwner()
    {
        var root = CreateRoot();
        var runtime = Path.Combine(root, "must-not-be-created");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "serve", "--runtime-root", runtime, "--port", "0", "--native-client-pid", int.MaxValue.ToString() })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(deadline.Token);
        Assert.Equal(1, process.ExitCode);
        Assert.False(Directory.Exists(runtime));
    }

    [Fact(DisplayName = "Actual Host Authorizes Only Its Registered Socket Owner And Rejects Replay And Invalid Actions")]
    public async Task AuthorizesSocketOwner()
    {
        var root = CreateRoot();
        using var host = await HostRun.StartAsync(Path.Combine(root, "runtime"), Environment.ProcessId);
        using var http = new HttpClient { BaseAddress = host.Address };
        var session = await ReadSessionAsync(http, host.VerificationKey!, root);
        foreach (var invalidHandshake in new[] { "control/v1/desktop/session", "control/v1/desktop/session?correlationId=invalid",
                     "control/v1/desktop/session?correlationId=" + Guid.NewGuid() + "&unrecognized=true" })
        {
            using var invalid = await http.GetAsync(invalidHandshake);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var request = Action(session, "file.select");
        using var approved = await http.PostAsJsonAsync("control/v1/desktop/authorize", request);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var reply = new DesktopProofReader(new ContractReader()).Read(await approved.Content.ReadAsByteArrayAsync(),
            host.VerificationKey!, "desktop-channel.schema.json");
        Assert.True(reply.RootElement.GetProperty("isApproved").GetBoolean());
        Assert.Equal((string)request["correlationId"], reply.RootElement.GetProperty("correlationId").GetString());
        using var duplicate = await http.PostAsJsonAsync("control/v1/desktop/authorize", request);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        request["correlationId"] = Guid.NewGuid().ToString();
        request["type"] = "show";
        using var close = await http.PostAsJsonAsync("control/v1/desktop/authorize", request);
        Assert.Equal(HttpStatusCode.Forbidden, close.StatusCode);
        request["type"] = "external.open";
        request["url"] = "https://user:secret@example.invalid/";
        using var credentials = await http.PostAsJsonAsync("control/v1/desktop/authorize", request);
        Assert.Equal(HttpStatusCode.Conflict, credentials.StatusCode);
        request["url"] = "https://example.invalid/help";
        using var external = await http.PostAsJsonAsync("control/v1/desktop/authorize", request);
        Assert.Equal(HttpStatusCode.OK, external.StatusCode);
        using var malformed = await http.PostAsync("control/v1/desktop/authorize", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var excessive = await http.PostAsync("control/v1/desktop/authorize", new StringContent(new string(' ', 8193), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, excessive.StatusCode);
        using var wrongMedia = await http.PostAsync("control/v1/desktop/authorize", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongMedia.StatusCode);
        await AssertDeniedAsync(root, host.Address, Action(session, "folder.select"));
        Console.WriteLine(JsonSerializer.Serialize(new { scope = "actual Host native authorization, no native UI action", root, host.Identity }));
    }

    [Fact(DisplayName = "Actual Host Restart Invalidates The Previous Desktop Generation And Default Host Has No Native Registration")]
    public async Task RevokesRestartedGeneration()
    {
        var root = CreateRoot();
        var runtime = Path.Combine(root, "runtime");
        JsonElement previous;
        using (var first = await HostRun.StartAsync(runtime, Environment.ProcessId))
        using (var http = new HttpClient { BaseAddress = first.Address }) previous = await ReadSessionAsync(http, first.VerificationKey!);
        using (var next = await HostRun.StartAsync(runtime, Environment.ProcessId))
        using (var http = new HttpClient { BaseAddress = next.Address })
        {
            var current = await ReadSessionAsync(http, next.VerificationKey!);
            Assert.Equal(previous.GetProperty("instanceId").GetString(), current.GetProperty("instanceId").GetString());
            Assert.NotEqual(previous.GetProperty("sessionId").GetString(), current.GetProperty("sessionId").GetString());
            using var stale = await http.PostAsJsonAsync("control/v1/desktop/authorize", Action(previous, "file.select"));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var fresh = await http.PostAsJsonAsync("control/v1/desktop/authorize", Action(current, "file.select"));
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }
        using var unregistered = await HostRun.StartAsync(Path.Combine(root, "browser-runtime"), null);
        using var browser = new HttpClient { BaseAddress = unregistered.Address };
        using var denied = await browser.GetAsync("control/v1/desktop/session");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    private static async Task AssertDeniedAsync(string root, Uri address, Dictionary<string, object> request)
    {
        var input = Path.Combine(root, "forged-request.json");
        var output = Path.Combine(root, "forged-response.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(request));
        using var job = new OwnedWorkerJob();
        job.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
            ["desktop-http", address.AbsoluteUri, input, output, Environment.ProcessId.ToString()], root,
            Guid.NewGuid(), Guid.NewGuid(), identity => File.WriteAllText(Path.Combine(root, "foreign-identity.json"), JsonSerializer.Serialize(identity)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(output)) await Task.Delay(20, deadline.Token);
        using var result = JsonDocument.Parse(File.ReadAllBytes(output));
        Assert.Equal(403, result.RootElement.GetProperty("sessionStatus").GetInt32());
        Assert.Equal(403, result.RootElement.GetProperty("actionStatus").GetInt32());
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<JsonElement> ReadSessionAsync(HttpClient http, string verificationKey, string? corpusRoot = null)
    {
        var challenge = Guid.NewGuid().ToString("D");
        using var response = await http.GetAsync("control/v1/desktop/session?correlationId=" + challenge);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var document = new DesktopProofReader(new ContractReader()).Read(bytes,
            verificationKey, "desktop-session.schema.json");
        Assert.Equal(challenge, document.RootElement.GetProperty("correlationId").GetString());
        if (corpusRoot is not null)
        {
            File.WriteAllBytes(Path.Combine(corpusRoot, "signed-session-response.json"), bytes);
            File.WriteAllText(Path.Combine(corpusRoot, "verification-key.txt"), verificationKey);
        }
        return document.RootElement.Clone();
    }

    private static Dictionary<string, object> Action(JsonElement session, string type) => new()
    {
        ["format"] = "nexus.desktop.channel", ["version"] = 1,
        ["instanceId"] = session.GetProperty("instanceId").GetString()!,
        ["clientId"] = session.GetProperty("clientId").GetString()!,
        ["sessionId"] = session.GetProperty("sessionId").GetString()!,
        ["correlationId"] = Guid.NewGuid().ToString(), ["type"] = type
    };

    private static string CreateRoot()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "desktop-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal sealed class HostRun(Process process, Uri address, ProcessIdentity identity, string? verificationKey) : IDisposable
    {
        public Uri Address { get; } = address;
        public ProcessIdentity Identity { get; } = identity;
        public string? VerificationKey { get; } = verificationKey;
        public int ExitCode => process.ExitCode;
        public Task AwaitExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

        public static async Task<HostRun> StartAsync(string runtime, int? nativePid)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "serve", "--runtime-root", runtime, "--port", "0" }) start.ArgumentList.Add(argument);
            if (nativePid is { } pid) { start.ArgumentList.Add("--native-client-pid"); start.ArgumentList.Add(pid.ToString()); }
            var child = Process.Start(start) ?? throw new IOException("Host was not started");
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var line = await child.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) ??
                    throw new IOException(await child.StandardError.ReadToEndAsync(deadline.Token).ConfigureAwait(false));
                using var ready = JsonDocument.Parse(line);
                Assert.Equal(child.Id, ready.RootElement.GetProperty("processId").GetInt32());
                return new(child, new Uri(ready.RootElement.GetProperty("address").GetString()!), ProcessIdentityReader.Read(child.Id),
                    ready.RootElement.GetProperty("desktopVerificationKey").GetString());
            }
            catch { if (!child.HasExited) child.Kill(); child.WaitForExit(); child.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit();
            process.Dispose();
        }
    }
}
