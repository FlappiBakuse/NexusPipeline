using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
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
public sealed class FrontendClientTests
{
    [Fact(DisplayName = "Independent Host Issued Clients Remain Bound To Their Original Subject Origin And Current Authority")]
    public void BindsCurrentAuthority()
    {
        var revoked = false;
        var caller = new AuthenticatedControlRequest(ControlOrigin.Remote, "remote:one", null, () =>
            !revoked ? new(false, false) : throw new UnauthorizedAccessException("fixture_revoked"));
        var clients = new FrontendClients(TimeProvider.System);
        var first = clients.Create(caller, "http://one"); var second = clients.Create(caller, "http://one");
        Assert.NotEqual(first.ClientId, second.ClientId); Assert.NotEqual(first.Token, second.Token);
        var accepted = clients.Resolve(first.Token, caller, "http://one");
        Assert.Equal(first.ClientId, accepted.ClientId);
        Assert.Throws<UnauthorizedAccessException>(() => clients.Resolve(first.Token, Caller("remote:two"), "http://one"));
        Assert.Throws<UnauthorizedAccessException>(() => clients.Resolve(first.Token, caller, "http://two"));
        Assert.Throws<UnauthorizedAccessException>(() => clients.Resolve(first.ClientId.ToString("D"), caller, "http://one"));
        revoked = true;
        Assert.Throws<UnauthorizedAccessException>(accepted.RequireCurrent);
        Assert.True(accepted.Lifetime.IsCancellationRequested);
    }

    [Fact(DisplayName = "Retirement And Expiry Invalidate Already Accepted Client Work Without Revoking Other Clients")]
    public void RetiresAcceptedClient()
    {
        var clock = new Clock(); var clients = new FrontendClients(clock); var caller = Caller("remote:one");
        var first = clients.Create(caller, "http://one"); var second = clients.Create(caller, "http://one");
        var accepted = clients.Resolve(first.Token, caller, "http://one");
        clients.Retire(first.Token, caller, "http://one");
        Assert.Throws<UnauthorizedAccessException>(accepted.RequireCurrent);
        Assert.True(accepted.Lifetime.IsCancellationRequested);
        clients.Resolve(second.Token, caller, "http://one").RequireCurrent();
        clock.Now = second.ExpiresAt;
        Assert.Throws<UnauthorizedAccessException>(() => clients.Resolve(second.Token, caller, "http://one"));
    }

    [Fact(DisplayName = "The Host Bounds Live Frontend Clients And Reclaims Only Expired Grants")]
    public void BoundsLiveClients()
    {
        var clock = new Clock(); var clients = new FrontendClients(clock); var caller = Caller("remote:one");
        var first = clients.Create(caller, "http://one");
        for (var index = 1; index < 128; index++) _ = clients.Create(caller, "http://one");
        Assert.Throws<ControlRequestException>(() => clients.Create(caller, "http://one"));
        clients.Resolve(first.Token, caller, "http://one").RequireCurrent();
        clock.Now = first.ExpiresAt;
        var fresh = clients.Create(caller, "http://one");
        Assert.NotEqual(first.ClientId, fresh.ClientId);
        Assert.Throws<UnauthorizedAccessException>(() => clients.Resolve(first.Token, caller, "http://one"));
    }

    [Fact(DisplayName = "Actual Authenticated Http Issues Distinct Opaque Client Grants And Rejects Retired Grant Headers")]
    public async Task UsesAuthenticatedHttp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var authority = new ControlAuthentication(Guid.NewGuid(), false, null,
            () => new(0, HostSettings.Default), null!, TimeProvider.System);
        var clients = new FrontendClients(TimeProvider.System);
        app.Use((context, next) => authority.InvokeAsync(context, next));
        clients.Map(app, authority);
        app.MapGet("/control/v1/client-proof", (HttpContext context) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead);
            try { return Results.Json(new { clientId = clients.Read(context, caller)?.ClientId }); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(address) };
        async Task<FrontendClients.ClientReceipt> Create()
        {
            using var response = await http.PostAsync("/control/v1/extensions/clients", null);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<FrontendClients.ClientReceipt>())!;
        }
        var first = await Create(); var second = await Create();
        Assert.NotEqual(first.ClientId, second.ClientId);
        http.DefaultRequestHeaders.Add("X-Nexus-Frontend-Client", first.Token);
        using (var proof = await http.GetAsync("/control/v1/client-proof"))
            Assert.Contains(first.ClientId.ToString("D"), await proof.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using (var retired = await http.PostAsync("/control/v1/extensions/clients/retire", null))
            Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);
        using (var denied = await http.GetAsync("/control/v1/client-proof")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static AuthenticatedControlRequest Caller(string subject) => new(ControlOrigin.Remote, subject, null, () => new(false, false));
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
