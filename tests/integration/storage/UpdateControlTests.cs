using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Tests.Fixtures;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class UpdateControlTests
{
    [Theory(DisplayName = "Actual Local Update Feed Http Returns Json Null For An Empty Cache And Preserves Signed Cached Feeds Without Network Access")]
    [InlineData("stable", false)]
    [InlineData("prerelease", false)]
    [InlineData("stable", true)]
    [InlineData("prerelease", true)]
    public async Task ReadsCachedFeed(string channel, bool cached)
    {
        using var owner = new ExtensionCatalogFixture();
        using var input = new UpdateFeedFixture();
        var version = channel == "stable" ? "0.18.0" : "0.18.0-beta.1";
        if (cached)
        {
            var signed = input.Signed(input.Document(channel: channel, version: version));
            var feed = input.Verifier().Verify(signed.Feed, signed.Signature,
                channel == "stable" ? HostUpdateChannel.Stable : HostUpdateChannel.Prerelease);
            new UpdateFeedStore(owner.Instance, owner.Database, owner.Lease, owner.Resources, owner.Contracts, input.Verifier())
                .Accept(Guid.NewGuid(), "fixture", feed);
        }
        var operations = owner.Count("operations");
        var trust = new UpdateReleaseTrust(new Dictionary<string, byte[]> { ["fixture-only"] = input.Signer.ExportSubjectPublicKeyInfo() },
            UpdateFeedFixture.FeedRoot, UpdateFeedFixture.StoreRoot, [UpdateFeedFixture.Artifacts], 1, 1);
        await using var updates = new HostUpdateService(owner.Instance, owner.Database, owner.Lease, owner.Resources, owner.Contracts,
            new PayloadApplyRoots(Path.GetDirectoryName(owner.Instance.RuntimeRoot)!, Path.Combine(owner.Instance.RuntimeRoot, "extensions")),
            trust, () => throw new IOException("unexpected_update_network_request"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var authority = new ControlAuthentication(owner.Instance.InstanceId, false, null,
            () => new(0, HostSettings.Default), null!, TimeProvider.System);
        app.Use((context, next) => authority.InvokeAsync(context, next));
        new HostUpdateControl(authority, updates, owner.Contracts).Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(address) };
        using var response = await http.GetAsync("/control/v1/maintenance/feed/" + channel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var result = JsonDocument.Parse(bytes);
        if (cached)
        {
            Assert.Equal(channel, result.RootElement.GetProperty("identity").GetProperty("channel").GetString());
            Assert.Equal(1, result.RootElement.GetProperty("identity").GetProperty("sequence").GetInt64());
            Assert.Equal(version, result.RootElement.GetProperty("releases")[0].GetProperty("hostVersion").GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, result.RootElement.ValueKind);
            Assert.Equal("null"u8.ToArray(), bytes);
        }
        Assert.Equal(operations, owner.Count("operations"));
    }
}
