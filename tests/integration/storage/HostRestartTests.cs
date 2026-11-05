using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class HostRestartTests
{
    [Fact]
    public async Task RestartsSavedPort()
    {
        using var origin = MaintenanceStartupOrigin.CaptureUnjobbedProcess();
        origin.RequireCurrent();
        var executable = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE") ?? throw new InvalidOperationException();
        var runtime = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "restart-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var item in new[] { "serve", "--runtime-root", runtime, "--port", "0" }) start.ArgumentList.Add(item);
        start.Environment.Remove("DOTNET_STARTUP_HOOKS");
        using var parent = Process.Start(start) ?? throw new IOException("host_not_started");
        var errors = parent.StandardError.ReadToEndAsync();
        Process? successor = null;
        try
        {
            var line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(string.IsNullOrEmpty(line));
            using var ready = JsonDocument.Parse(line!);
            var identity = ready.RootElement.GetProperty("instanceId").GetGuid();
            var address = ready.RootElement.GetProperty("address").GetString()!;
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(40) };
            using var settings = JsonDocument.Parse(await client.GetStringAsync("/control/v1/settings/service"));
            var port = FreePort();
            var service = settings.RootElement.GetProperty("service").Deserialize<Dictionary<string, JsonElement>>()!;
            service["webPort"] = JsonSerializer.SerializeToElement(port);
            using var saved = await client.PostAsJsonAsync("/control/v1/settings/service", new
            {
                format = "nexus.control.service-settings", version = 1, operationId = Guid.NewGuid(),
                expectedRevision = settings.RootElement.GetProperty("revision").GetInt64(), service
            });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var operation = Guid.NewGuid();
            using var restart = await client.PostAsync("/control/v1/maintenance/restart/" + operation.ToString("D"), null);
            var response = await restart.Content.ReadAsStringAsync();
            Assert.True(restart.StatusCode == HttpStatusCode.Accepted, response);
            using var receipt = JsonDocument.Parse(response);
            Assert.Equal(operation, receipt.RootElement.GetProperty("operationId").GetGuid());
            Assert.Equal(identity, receipt.RootElement.GetProperty("instanceId").GetGuid());
            Assert.Equal(port, receipt.RootElement.GetProperty("port").GetInt32());
            Assert.Equal("handoff", receipt.RootElement.GetProperty("state").GetString());
            var pid = receipt.RootElement.GetProperty("processId").GetInt32();
            Assert.NotEqual(parent.Id, pid);
            successor = Process.GetProcessById(pid);
            _ = successor.SafeHandle;
            var actual = ProcessIdentityReader.Read(pid);
            Assert.Equal(Path.GetFullPath(executable), actual.ImagePath, ignoreCase: true);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, parent.ExitCode);
            var nextAddress = "http://127.0.0.1:" + port;
            using var next = new HttpClient { BaseAddress = new Uri(nextAddress), Timeout = TimeSpan.FromSeconds(2) };
            var status = await WaitReadyAsync(next, successor);
            using (status)
            {
                Assert.Equal(identity, status.RootElement.GetProperty("instanceId").GetGuid());
                Assert.Equal(pid, status.RootElement.GetProperty("processId").GetInt32());
            }
            Assert.Equal(actual, ProcessIdentityReader.Read(pid));
            using var current = JsonDocument.Parse(await next.GetStringAsync("/control/v1/settings/service"));
            Assert.Equal(port, current.RootElement.GetProperty("service").GetProperty("webPort").GetInt32());
            Console.WriteLine($"restart: old={parent.Id}; successor={pid}; port={port}; instance={identity:D}");
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
            if (successor is not null)
            {
                if (!successor.HasExited) { successor.Kill(); await successor.WaitForExitAsync(); }
                successor.Dispose();
            }
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

    private static async Task<JsonDocument> WaitReadyAsync(HttpClient client, Process process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!deadline.IsCancellationRequested)
        {
            Assert.False(process.HasExited, "Prepared successor exited before listener readiness");
            try { return JsonDocument.Parse(await client.GetStringAsync("/control/v1/status", deadline.Token)); }
            catch (HttpRequestException) { await Task.Delay(50, deadline.Token); }
        }
        throw new TimeoutException("restart_listener_not_ready");
    }
}
