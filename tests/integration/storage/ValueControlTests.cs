using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ValueControlTests
{
    [Fact(DisplayName = "Generic Structured Reads Use Verified Saved Bytes And Reject Stale Resource Identity Without Mutation")]
    public void ReadsSavedDocument()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var service = new ValueService(source.Instance, source.Database, new(), source.Lease, fixture.Resources);
        var resource = Assert.Single(service.Read(source.BindingA));
        var file = Assert.Single(service.ReadFiles(source.BindingA, resource.ResourceId, resource.Revision).Items);
        var before = fixture.Counts();
        var document = service.ReadDocument(source.BindingA, resource.ResourceId, resource.Revision, "", file.Sha256, ConfigurationSyntax.Json);
        Assert.True(document.Value.GetProperty("fixture").GetBoolean());
        Assert.Equal(file.Sha256, document.SourceSha256);
        Assert.Equal(resource.Revision, document.Revision);
        Assert.Throws<ConfigurationException>(() => service.ReadDocument(source.BindingA, resource.ResourceId,
            resource.Revision + 1, "", file.Sha256, ConfigurationSyntax.Json));
        Assert.Throws<ConfigurationException>(() => service.ReadDocument(source.BindingA, resource.ResourceId,
            resource.Revision, "", new string('0', 64), ConfigurationSyntax.Json));
        Assert.Throws<ConfigurationException>(() => service.ReadDocument(source.BindingA, resource.ResourceId,
            resource.Revision, "", file.Sha256, ConfigurationSyntax.Opaque));
        Assert.Equal(before, fixture.Counts());
    }

    [Fact(DisplayName = "Real Generic Saved Value Edits Keep Live Bytes And Replay The Original Operation After Later Binding Changes")]
    public async Task ReplaysSavedEdits()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var service = new ValueService(source.Instance, source.Database, new(), source.Lease, fixture.Resources);
        var resource = Assert.Single(service.Read(source.BindingA));
        var file = Assert.Single(service.ReadFiles(source.BindingA, resource.ResourceId, resource.Revision).Items); var live = File.ReadAllBytes(source.Configuration);
        var operation = Guid.NewGuid();
        var request = Bytes(operation, resource.ResourceId, file.Sha256, true, false);
        var before = fixture.Counts();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var authority = new ControlAuthentication(source.Instance.InstanceId, false, null, () => new(0, HostSettings.Default), null!, TimeProvider.System);
        app.Use(ControlExceptionBoundary.InvokeAsync); app.Use(authority.InvokeAsync);
        new ConfigurationValueControl(authority, new(service), new()).Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(address) };
        var route = $"/control/v1/configuration/generic/{source.BindingA:D}/value/";
        var wire = new JsonObject { ["format"] = "nexus.control.configuration-value", ["version"] = 1,
            ["expectedBindingRevision"] = 1, ["relativePath"] = "", ["syntax"] = "json", ["patch"] = JsonNode.Parse(request) };
        var invalid = wire.DeepClone(); invalid["patch"]!["unexpected"] = true;
        using (var rejected = await http.PostAsJsonAsync(route + "apply", invalid)) Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var preview = await http.PostAsJsonAsync(route + "preview", wire))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.True((await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasChanges").GetBoolean());
        }
        Assert.Equal(before, fixture.Counts());
        using var identity = WindowsIdentity.GetCurrent();
        var subject = "local:" + identity.User!.Value;
        var result = service.Apply(subject, source.BindingA, 1, "", ConfigurationSyntax.Json, request, DateTimeOffset.UtcNow);
        Assert.Equal(2, result.Saved.Revision); Assert.Equal(live, File.ReadAllBytes(source.Configuration));
        var changed = Assert.Single(result.Saved.Tree.Entries);
        using (var content = new ImmutableBlobStore(source.Instance, new()).OpenVerified(new($"blobs/content/{changed.Sha256}.bin", changed.SizeBytes, changed.Sha256!)))
        using (var json = JsonDocument.Parse(content)) Assert.False(json.RootElement.GetProperty("fixture").GetBoolean());
        var catalog = source.Definitions.Read(); var binding = catalog.Bindings.Single(item => item.Id == source.BindingA);
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [binding with { Priority = binding.Priority + 1 }], [], [], []);
        source.Definitions.Commit(NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore.Identity(
            Guid.NewGuid(), "generic-control-fixture", changes), changes, DateTimeOffset.UtcNow);
        using (var replay = await http.PostAsJsonAsync(route + "apply", wire))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var body = await replay.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(operation, body.GetProperty("operationId").GetGuid());
            Assert.False(body.GetProperty("isNew").GetBoolean());
            Assert.Equal(result.Saved.Revision, body.GetProperty("revision").GetInt64());
        }
        Assert.Equal(live, File.ReadAllBytes(source.Configuration));
        Assert.Throws<OperationConflictException>(() => service.Apply("foreign", source.BindingA, 1, "",
            ConfigurationSyntax.Json, request, DateTimeOffset.UtcNow));
        Assert.Equal(2, Assert.Single(service.Read(source.BindingA)).Revision);
    }

    [Fact(DisplayName = "A Real Definition Mutation And An Undeclared Generic Resource Cannot Borrow Value Write Authority")]
    public void RejectsUndeclaredResources()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var service = new ValueService(source.Instance, source.Database, new(), source.Lease, fixture.Resources);
        var resource = Assert.Single(service.Read(source.BindingA)); var file = Assert.Single(service.ReadFiles(source.BindingA, resource.ResourceId, resource.Revision).Items);
        var bytes = Bytes(Guid.NewGuid(), resource.ResourceId, file.Sha256, true, false);
        var before = fixture.Counts(); var live = File.ReadAllBytes(source.Configuration);
        using (var mutation = fixture.Resources.TryAcquire(Guid.NewGuid(),
            [new("automation:definitions", NexusPipeline.Core.Maintenance.ResourceClaimMode.Mutation)]).Lease!)
            Assert.Equal("configuration_resource_conflict", Assert.Throws<ConfigurationException>(() => service.Apply(
                "generic-control-fixture", source.BindingA, 1, "", ConfigurationSyntax.Json, bytes, DateTimeOffset.UtcNow)).Message);
        Assert.Throws<ConfigurationException>(() => service.Apply("generic-control-fixture", source.OtherBinding, 1, "",
            ConfigurationSyntax.Json, bytes, DateTimeOffset.UtcNow));
        Assert.Equal(before, fixture.Counts()); Assert.Equal(live, File.ReadAllBytes(source.Configuration));
        Assert.Equal(1, Assert.Single(service.Read(source.BindingA)).Revision);
    }

    private static byte[] Bytes(Guid operation, string resource, string source, bool expected, bool value) =>
        JsonSerializer.SerializeToUtf8Bytes(new { format = "nexus.config.patch", version = 1,
            operationId = operation, resourceId = resource, sourceSha256 = source, expectedRevision = 1,
            edits = new[] { new { selector = new[] { "fixture" }, expected, value } } });
}
