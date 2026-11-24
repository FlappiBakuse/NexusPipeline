using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Control.Http;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ControlAuthenticationTests
{
    [Fact(DisplayName = "Two Hundred Actual Local Http Requests Never Read Service Settings For Capabilities")]
    public async Task SkipsLocalSettings()
    {
        string[] localCapabilities = [
            "automation.declare", "automation.delete", "automation.exchange", "automation.read", "automation.write",
            "completion.local", "configuration.edit", "configuration.native.open", "configuration.read", "configuration.repair",
            "control.capabilities.read", "control.doctor.read", "control.status.read", "desktop.local",
            "execution.cancel", "execution.preview", "execution.start", "extensions.read", "extensions.write",
            "maintenance.read", "maintenance.write", "media.preview.grant",
            "notifications.delete", "notifications.read", "notifications.test", "notifications.write", "operations.read",
            "records.images.read", "records.logs.read", "records.read", "records.retire", "scheduling.read",
            "secrets.read", "secrets.status", "secrets.write", "settings.read", "settings.write"
        ];
        var reads = 0;
        await using var server = await StartAsync(port => new ControlAuthentication(Guid.NewGuid(), false, null,
            () => { Interlocked.Increment(ref reads); return new(0, HostSettings.Default); }, null!, TimeProvider.System));
        for (var index = 0; index < 200; index++)
        {
            using var response = await server.Client.GetAsync("control/v1/check");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var capabilities = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(localCapabilities, capabilities.RootElement.EnumerateArray()
                .Select(item => item.GetString()).Order(StringComparer.Ordinal));
        }
        Assert.Equal(0, Volatile.Read(ref reads));
    }

    [Fact(DisplayName = "Actual Mcp Http Admission And Capabilities Read Each New Service State")]
    public async Task RechecksMcpSettings()
    {
        var reads = 0;
        var values = HostSettings.Default;
        await using var server = await StartAsync(port => new ControlAuthentication(Guid.NewGuid(), false, port,
            () => { Interlocked.Increment(ref reads); return new(0, values); }, null!, TimeProvider.System));
        var before = reads;
        using (var denied = await server.Client.GetAsync("control/v1/mcp"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(reads > before);
        values = values with { Service = values.Service with { McpEnabled = true } };
        before = reads;
        using (var allowed = await server.Client.GetAsync("control/v1/mcp"))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            using var names = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
            Assert.DoesNotContain(names.RootElement.EnumerateArray(), item => item.GetString() == "notifications.write");
        }
        Assert.True(reads > before);
        values = values with { Service = values.Service with { McpWriteEnabled = true } };
        before = reads;
        using (var writable = await server.Client.GetAsync("control/v1/mcp"))
        {
            Assert.Equal(HttpStatusCode.OK, writable.StatusCode);
            using var names = JsonDocument.Parse(await writable.Content.ReadAsStringAsync());
            Assert.Contains(names.RootElement.EnumerateArray(), item => item.GetString() == "notifications.write");
        }
        Assert.True(reads > before);
        values = values with { Service = values.Service with { McpEnabled = false } };
        before = reads;
        using (var revoked = await server.Client.GetAsync("control/v1/mcp"))
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.True(reads > before);
    }

    [Fact(DisplayName = "Remote Admission Reads Fresh Service State Without A Bearer")]
    public async Task RechecksRemoteAdmission()
    {
        var values = HostSettings.Default;
        await using var app = WebApplication.CreateBuilder().Build();
        var authority = new ControlAuthentication(Guid.NewGuid(), true, null,
            () => new(0, values), null!, TimeProvider.System);
        async Task<int> Request()
        {
            var context = new DefaultHttpContext();
            context.Connection.LocalIpAddress = IPAddress.Loopback;
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
            context.Connection.LocalPort = 58731;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("127.0.0.1", 58731);
            context.Request.Path = "/control/v1/check";
            context.RequestServices = app.Services;
            using var body = new MemoryStream();
            context.Response.Body = body;
            await authority.InvokeAsync(context, _ => throw new Xunit.Sdk.XunitException("remote_request_passed_without_bearer"));
            body.Position = 0;
            using var error = await JsonDocument.ParseAsync(body);
            Assert.Equal("access_denied", error.RootElement.GetProperty("code").GetString());
            return context.Response.StatusCode;
        }
        Assert.Equal(403, await Request());
        values = values with { Service = values.Service with { AllowRemoteAccess = true } };
        Assert.Equal(401, await Request());
        values = values with { Service = values.Service with { AllowRemoteAccess = false } };
        Assert.Equal(403, await Request());
    }

    private static async Task<HttpScope> StartAsync(Func<int, ControlAuthentication> create)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        ControlAuthentication? authority = null;
        app.Use((context, next) => authority!.InvokeAsync(context, next));
        app.MapGet("/control/v1/check", (HttpContext context) =>
        {
            var request = authority!.Require(context, ControlCapability.StatusRead);
            return Results.Json(request.Visible().Select(item => item.Name));
        });
        app.MapGet("/control/v1/mcp", (HttpContext context) =>
        {
            var request = authority!.Require(context, ControlCapability.CapabilitiesRead);
            return Results.Json(request.Visible().Select(item => item.Name));
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var uri = new Uri(address);
        authority = create(uri.Port);
        return new(app, new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = uri });
    }

    private sealed record HttpScope(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
