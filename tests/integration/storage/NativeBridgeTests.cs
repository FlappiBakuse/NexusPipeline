using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Composition;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Platform.Networking;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NativeBridgeTests
{
    [Fact(DisplayName = "Actual Electron Main Frame IPC And Native Tcp Bridge Complete And Cancel An Installed SDK Login Without Publishing Secret Json")]
    public async Task CapturesNativeBridge()
    {
        using var catalog = new ExtensionCatalogFixture(); using var clients = new NativeClientSessions(catalog.Instance.InstanceId);
        await using var captures = new BrowserLoginSessions();
        using var listener = new ControlListener(IPAddress.Loopback, 0, null);
        var origin = $"http://127.0.0.1:{listener.Port}";
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(null, null, null, null, null, null, NativeClients: clients, BrowserLogins: captures),
            managedManifestEdit: manifest =>
            {
                manifest["permissions"]!["desktopAccess"] = new JsonArray("interactive-login");
                manifest["permissions"]!["loginOrigins"] = new JsonArray(origin);
            });
        File.WriteAllText(Path.Combine(fixture.ActivationDirectory, "browser-capture-origin"), origin);
        _ = await FrontendInstalledTests.OpenAsync(fixture);
        var output = Path.Combine(fixture.ActivationDirectory, "native-bridge"); Directory.CreateDirectory(output);
        var child = StartElectron(output); using var process = child.Process;
        using var binding = new NativeClientBinding(process.Id);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = output });
        builder.Logging.ClearProviders(); builder.WebHost.UseSockets(options => options.CreateBoundListenSocket = listener.CreateSocket);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, listener.Port));
        await using var app = builder.Build();
        await using var desktop = new DesktopControl(binding, catalog.Instance.InstanceId, new(), catalog.Lifecycle,
            new DesktopExitWatch(binding, catalog.Lifecycle, app.Lifetime), nativeClients: clients);
        var authority = new ControlAuthentication(catalog.Instance.InstanceId, false, null, () => new(0, HostSettings.Default), null!, TimeProvider.System);
        app.Use(ControlExceptionBoundary.InvokeAsync); app.Use(authority.InvokeAsync);
        var frontend = new ExtensionFrontendControl(authority, fixture.Activator); frontend.Map(app);
        desktop.Map(app); new NativeFrontendControl(authority, frontend.Clients, desktop, clients).Map(app);
        new DesktopLoginControl(authority, desktop, captures, new()).Map(app);
        MapPages(app, fixture.ActivationDirectory);
        await app.StartAsync();
        var activation = fixture.Activator.RequirePublishedRuntime("fixture-plugin").ActivationId;
        File.WriteAllBytes(Path.Combine(output, "input.json"), JsonSerializer.SerializeToUtf8Bytes(new { address = origin + "/", instanceId = catalog.Instance.InstanceId,
            verificationKey = desktop.VerificationKeyBase64, activationId = activation, route = "/" }));
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(55));
            await Task.WhenAll(child.Logs);
            using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "receipt.json")));
            Assert.Equal("PASS", report.RootElement.GetProperty("status").GetString());
            Assert.NotEmpty(report.RootElement.GetProperty("checks").EnumerateArray());
            Assert.Equal(0, process.ExitCode);
            await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Task.WhenAll(child.Logs);
            await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); await app.StopAsync();
        }
    }

    private static void MapPages(WebApplication app, string directory)
    {
        app.MapGet("/", () => Results.Content("<!doctype html><title>Native bridge fixture</title>", "text/html"));
        app.MapGet("/login-fixture", (HttpContext context) =>
        {
            context.Response.Headers.Append("Set-Cookie", "token=synthetic; Path=/; HttpOnly; SameSite=Lax");
            return Results.Content("<!doctype html><title>Synthetic login</title>", "text/html");
        });
        app.MapGet("/login-incomplete", () => Results.Content("<!doctype html><title>Incomplete login</title>", "text/html"));
        app.MapGet("/fixture/drained/{operation}", (string operation) =>
        {
            if (!Guid.TryParseExact(operation, "D", out var id)) return Results.BadRequest();
            return Results.Json(new { drained = File.Exists(Path.Combine(directory, "browser-ended-" + id.ToString("D"))) });
        });
    }

    private static (Process Process, Task[] Logs) StartElectron(string output)
    {
        var runtime = Environment.GetEnvironmentVariable("NEXUS_LOGIN_RUNTIME") ?? throw new IOException("NEXUS_LOGIN_RUNTIME_required");
        var script = Environment.GetEnvironmentVariable("NEXUS_LOGIN_BRIDGE_SCRIPT") ?? throw new IOException("NEXUS_LOGIN_BRIDGE_SCRIPT_required");
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runtime, "runtime-receipt.json")));
        Assert.Equal("PASS", receipt.RootElement.GetProperty("status").GetString());
        var image = receipt.RootElement.GetProperty("files").EnumerateArray().Single(item => item.GetProperty("path").GetString() == "electron.exe");
        var executable = Path.Combine(runtime, "electron.exe");
        using (var bytes = File.OpenRead(executable)) Assert.Equal(image.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        File.Copy(Path.Combine(runtime, "runtime-receipt.json"), Path.Combine(output, "runtime-input.json"));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = output,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(script); start.ArgumentList.Add("--nexus-test-config=" + Path.Combine(output, "input.json"));
        start.Environment.Remove("ELECTRON_RUN_AS_NODE"); start.Environment["TEMP"] = output; start.Environment["TMP"] = output;
        var process = Process.Start(start) ?? throw new IOException("native_bridge_fixture_not_started");
        return (process, [SaveLog(process.StandardOutput, Path.Combine(output, "native.stdout.log")),
            SaveLog(process.StandardError, Path.Combine(output, "native.stderr.log"))]);
    }

    private static async Task SaveLog(StreamReader stream, string path) => await File.WriteAllTextAsync(path, await stream.ReadToEndAsync());
}
