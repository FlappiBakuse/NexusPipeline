using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Mcp;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class ScriptControlChecks
{
    internal static async Task VerifyAsync(ProjectSourceFixture project, InstallRuntimeFixture provider,
        ScriptProjectService scripts, ProjectPreviewService previews)
    {
        var catalog = project.Catalog;
        var compilation = new ProjectCompilationService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources,
            catalog.Contracts, provider.Roots, provider.Activator);
        var cases = new ProjectUseCases(compilation, previews, catalog.Contracts, scripts);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var authority = new ControlAuthentication(catalog.Instance.InstanceId, false, null, () => new(0, HostSettings.Default), null!, TimeProvider.System);
        app.Use(ControlExceptionBoundary.InvokeAsync); app.Use(authority.InvokeAsync);
        new ProjectControl(authority, cases, catalog.Contracts).Map(app);
        var frontends = new ExtensionFrontendControl(authority, provider.Activator); frontends.Map(app);
        new ProjectFrontendControl(catalog.Instance.InstanceId, authority, frontends, cases, catalog.Contracts).Map(app);
        await app.StartAsync();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var script = new AutomationDefinitionStore(catalog.Database).Read().Scripts.Single();
        var command = new JsonObject { ["format"] = "nexus.control.project-command", ["version"] = 1, ["action"] = "discover",
            ["scriptId"] = script.Id.ToString("D"), ["expectedScriptRevision"] = script.Revision, ["extensionId"] = "maas",
            ["selection"] = new JsonObject() };
        using (var response = await http.PostAsJsonAsync("/control/v1/projects/discover", command))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("Exists").GetBoolean());
        }
        command["bindingId"] = project.Binding.ToString("D");
        using (var response = await http.PostAsJsonAsync("/control/v1/projects/discover", command)) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        command.Remove("bindingId");
        var enabled = true;
        var caller = new AuthenticatedControlRequest(ControlOrigin.Mcp, "script-reader", null, () => new(enabled, true));
        var tools = new McpProjectTools(cases);
        var parameters = JsonSerializer.SerializeToElement(new { name = "project_discover", arguments = command });
        var reply = JsonSerializer.SerializeToElement(await tools.CallAsync(caller, parameters));
        Assert.True(reply.GetProperty("structuredContent").GetProperty("Exists").GetBoolean());
        enabled = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.CallAsync(caller, parameters));
        await FrontendAsync(http, project, provider, script.Id, script.Revision);
        await app.StopAsync();
    }

    private static async Task FrontendAsync(HttpClient http, ProjectSourceFixture project, InstallRuntimeFixture provider, Guid script, long revision)
    {
        var activation = provider.Activator.RequirePublishedRuntime("fixture-plugin").ActivationId;
        var route = "/control/v1/extensions/fixture-plugin/frontend/" + activation.ToString("D") + "/project/";
        var body = new JsonObject { ["context"] = new JsonObject { ["surfaceId"] = "automation.script.editor",
            ["instanceId"] = project.Catalog.Instance.InstanceId.ToString("D"), ["entityId"] = script.ToString("D") }, ["input"] = new JsonObject() };
        using (var denied = await http.PostAsJsonAsync(route + "context", body)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var grant = await http.PostAsync("/control/v1/extensions/clients", null);
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        http.DefaultRequestHeaders.Add("X-Nexus-Frontend-Client", (await grant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        using (var context = await http.PostAsJsonAsync(route + "context", body))
        {
            Assert.Equal(HttpStatusCode.OK, context.StatusCode);
            Assert.Equal(script, (await context.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scriptId").GetGuid());
        }
        var operation = Guid.NewGuid().ToString("D");
        body["input"] = new JsonObject { ["expectedScriptRevision"] = revision, ["expectedSelectionRevision"] = 1,
            ["selection"] = new JsonObject { ["mount"] = true, ["resourcePath"] = "assets" } };
        body["operationId"] = operation;
        using (var saved = await http.PostAsJsonAsync(route + "select", body)) Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var repeated = await http.PostAsJsonAsync(route + "select", body))
        {
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.False((await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isNew").GetBoolean());
        }
        body["input"]!["protectedInputs"] = new JsonArray();
        using (var denied = await http.PostAsJsonAsync(route + "select", body)) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        body["input"]!.AsObject().Remove("protectedInputs"); body.Remove("operationId"); body["input"]!["operationId"] = operation;
        using (var result = await http.PostAsJsonAsync(route + "result", body))
        {
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.True((await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("found").GetBoolean());
        }
        body["input"] = new JsonObject { ["expectedScriptRevision"] = revision };
        using var preview = await http.PostAsJsonAsync(route + "preview", body);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var id = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("previewId").GetString();
        body["input"] = new JsonObject { ["previewId"] = id, ["asset"] = "index" };
        using (var page = await http.PostAsJsonAsync(route + "page", body)) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        body["context"]!["surfaceId"] = "automation.binding.editor";
        body["context"]!["entityId"] = project.Binding.ToString("D");
        using (var denied = await http.PostAsJsonAsync(route + "page", body)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
