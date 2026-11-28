using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class HostRestartTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task RestartsSavedPort(bool changePort, bool externalDesktop)
    {
        var executable = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE") ?? throw new InvalidOperationException();
        var runtime = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "restart-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var item in new[] { "serve", "--runtime-root", runtime, "--port", "0" }) start.ArgumentList.Add(item);
        if (externalDesktop)
        {
            start.ArgumentList.Add("--native-client-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        start.Environment.Remove("DOTNET_STARTUP_HOOKS");
        using var parent = Process.Start(start) ?? throw new IOException("host_not_started");
        var errors = parent.StandardError.ReadToEndAsync();
        try
        {
            var line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(string.IsNullOrEmpty(line));
            using var ready = JsonDocument.Parse(line!);
            var identity = ready.RootElement.GetProperty("instanceId").GetGuid();
            var address = ready.RootElement.GetProperty("address").GetString()!;
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(40) };
            using var settings = JsonDocument.Parse(await client.GetStringAsync("/control/v1/settings/service"));
            var port = changePort ? FreePort() : new Uri(address).Port;
            var service = settings.RootElement.GetProperty("service").Deserialize<Dictionary<string, JsonElement>>()!;
            service["webPort"] = JsonSerializer.SerializeToElement(changePort ? port : 0);
            using var saved = await client.PostAsJsonAsync("/control/v1/settings/service", new
            {
                format = "nexus.control.service-settings", version = 1, operationId = Guid.NewGuid(),
                expectedRevision = settings.RootElement.GetProperty("revision").GetInt64(), service
            });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var oldFeed = JsonDocument.Parse(await client.GetStringAsync("/control/v1/events/snapshot"));
            var stream = oldFeed.RootElement.GetProperty("streamId").GetGuid();
            var after = oldFeed.RootElement.GetProperty("sequence").GetInt64();
            using var events = await client.GetAsync($"/control/v1/events?streamId={stream:D}&afterSequence={after}",
                HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            var operation = Guid.NewGuid();
            using var restart = await client.PostAsync("/control/v1/maintenance/restart/" + operation.ToString("D"), null);
            var response = await restart.Content.ReadAsStringAsync();
            if (externalDesktop)
            {
                Assert.Equal(HttpStatusCode.Conflict, restart.StatusCode);
                Assert.False(parent.HasExited);
                using var stillCurrent = JsonDocument.Parse(await client.GetStringAsync("/control/v1/events/snapshot"));
                Assert.Equal(stream, stillCurrent.RootElement.GetProperty("streamId").GetGuid());
                return;
            }
            Assert.True(restart.StatusCode == HttpStatusCode.Accepted, response);
            using var receipt = JsonDocument.Parse(response);
            Assert.Equal(operation, receipt.RootElement.GetProperty("operationId").GetGuid());
            Assert.Equal(identity, receipt.RootElement.GetProperty("instanceId").GetGuid());
            Assert.Equal(port, receipt.RootElement.GetProperty("port").GetInt32());
            Assert.Equal("restarting", receipt.RootElement.GetProperty("state").GetString());
            var pid = receipt.RootElement.GetProperty("processId").GetInt32();
            Assert.Equal(parent.Id, pid);
            var actual = ProcessIdentityReader.Read(pid);
            Assert.Equal(Path.GetFullPath(executable), actual.ImagePath, ignoreCase: true);
            var nextAddress = "http://127.0.0.1:" + port;
            using var next = new HttpClient { BaseAddress = new Uri(nextAddress), Timeout = TimeSpan.FromSeconds(2) };
            var status = await WaitReadyAsync(next, parent, operation);
            using (status)
            {
                Assert.Equal(identity, status.RootElement.GetProperty("instanceId").GetGuid());
                Assert.Equal(pid, status.RootElement.GetProperty("processId").GetInt32());
            }
            Assert.Equal(actual, ProcessIdentityReader.Read(pid));
            var observation = "/control/v1/maintenance/restart/" + operation.ToString("D");
            using var observed = new HttpRequestMessage(HttpMethod.Get, observation);
            observed.Headers.Add("Origin", address);
            using var readyReply = await next.SendAsync(observed);
            Assert.Equal(HttpStatusCode.OK, readyReply.StatusCode);
            Assert.Equal(address, Assert.Single(readyReply.Headers.GetValues("Access-Control-Allow-Origin")));
            using var unbound = new HttpRequestMessage(HttpMethod.Get, observation);
            unbound.Headers.Add("Origin", "http://localhost:" + new Uri(address).Port);
            using var denied = await next.SendAsync(unbound);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var current = JsonDocument.Parse(await next.GetStringAsync("/control/v1/settings/service"));
            Assert.Equal(changePort ? port : 0, current.RootElement.GetProperty("service").GetProperty("webPort").GetInt32());
            using var newFeed = JsonDocument.Parse(await next.GetStringAsync("/control/v1/events/snapshot"));
            Assert.NotEqual(stream, newFeed.RootElement.GetProperty("streamId").GetGuid());
            Console.WriteLine($"restart: retained host={pid}; port={port}; instance={identity:D}");
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
            Console.WriteLine(await errors);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task<JsonDocument> WaitReadyAsync(HttpClient client, Process process, Guid operation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!deadline.IsCancellationRequested)
        {
            Assert.False(process.HasExited, "The service restart must retain the original process");
            try
            {
                using var response = await client.GetAsync("/control/v1/maintenance/restart/" + operation.ToString("D"), deadline.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var ready = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                    Assert.Equal("ready", ready.RootElement.GetProperty("state").GetString());
                    return JsonDocument.Parse(await client.GetStringAsync("/control/v1/status", deadline.Token));
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(50, deadline.Token);
        }
        throw new TimeoutException("restart_listener_not_ready");
    }
}
