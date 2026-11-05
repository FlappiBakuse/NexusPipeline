using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DesktopModeTests
{
    [Fact(DisplayName = "Only The Original Native Socket Owner Can Confirm An Actual Desktop Mode And Failed Or Retired Requests Keep The Prior Effective Mode")]
    public async Task ConfirmsOwnedMode()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Use(ControlExceptionBoundary.InvokeAsync);
        var instance = Guid.NewGuid(); var recovery = new RecoveryIndex();
        recovery.PublishVerified(0, 1, []);
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System);
        var settings = HostSettings.Default;
        var runtime = new HostSettingsRuntime(new(0, settings), new(settings.Desktop.Mode, settings.Service, settings.Updates.PluginSource));
        var reject = false;
        using var binding = new NativeClientBinding(Environment.ProcessId);
        await using var desktop = new DesktopControl(binding, instance, new(), lifecycle,
            new DesktopExitWatch(binding, lifecycle, app.Lifetime), publishMode: mode =>
            {
                if (reject) throw new IOException("controlled_mode_failure");
                runtime.PublishDesktopMode(mode);
            });
        desktop.Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(address) };
        using var connected = await http.GetAsync("control/v1/desktop/session?correlationId=" + Guid.NewGuid());
        connected.EnsureSuccessStatusCode();
        using var session = new NexusPipeline.Platform.Security.DesktopProofReader(new ContractReader()).Read(
            await connected.Content.ReadAsByteArrayAsync(), desktop.VerificationKeyBase64, "desktop-session.schema.json");
        var request = new Dictionary<string, object>
        {
            ["format"] = "nexus.desktop.mode", ["version"] = 1, ["instanceId"] = instance,
            ["clientId"] = session.RootElement.GetProperty("clientId").GetGuid(),
            ["sessionId"] = session.RootElement.GetProperty("sessionId").GetGuid(),
            ["correlationId"] = Guid.NewGuid(), ["mode"] = "browser"
        };
        using (var accepted = await http.PostAsJsonAsync("/control/v1/desktop/mode", request)) accepted.EnsureSuccessStatusCode();
        Assert.Equal(DesktopMode.Browser, runtime.Read().Effective.Desktop.Mode);
        var forged = new Dictionary<string, object>(request) { ["sessionId"] = Guid.NewGuid(), ["mode"] = "desktop" };
        using (var denied = await http.PostAsJsonAsync("/control/v1/desktop/mode", forged)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(DesktopMode.Browser, runtime.Read().Effective.Desktop.Mode);
        request["mode"] = "desktop"; reject = true;
        using (var failed = await http.PostAsJsonAsync("/control/v1/desktop/mode", request)) Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal(DesktopMode.Browser, runtime.Read().Effective.Desktop.Mode);
        reject = false;
        await RejectForeign(address, request);
        Assert.Equal(DesktopMode.Browser, runtime.Read().Effective.Desktop.Mode);
        using (var accepted = await http.PostAsJsonAsync("/control/v1/desktop/mode", request)) accepted.EnsureSuccessStatusCode();
        Assert.Equal(DesktopMode.Desktop, runtime.Read().Effective.Desktop.Mode);
        binding.Dispose(); request["mode"] = "browser";
        using (var retired = await http.PostAsJsonAsync("/control/v1/desktop/mode", request)) Assert.Equal(HttpStatusCode.Forbidden, retired.StatusCode);
        Assert.Equal(DesktopMode.Desktop, runtime.Read().Effective.Desktop.Mode);
    }

    private static async Task RejectForeign(string address, Dictionary<string, object> request)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "desktop-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "request.json"); var output = Path.Combine(root, "reply.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(request));
        using var job = new OwnedWorkerJob();
        job.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
            ["desktop-http", address, input, output, Environment.ProcessId.ToString(), "mode"], root, Guid.NewGuid(), Guid.NewGuid(), _ => { });
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(output)) await Task.Delay(20, deadline.Token);
            using var result = JsonDocument.Parse(File.ReadAllBytes(output));
            Assert.Equal(403, result.RootElement.GetProperty("sessionStatus").GetInt32());
            Assert.Equal(403, result.RootElement.GetProperty("actionStatus").GetInt32());
        }
        finally { Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5))); }
    }
}
