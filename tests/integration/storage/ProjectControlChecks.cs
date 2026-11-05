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
using NexusPipeline.Host.Control.Cli;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Mcp;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class ProjectControlChecks
{
    internal static async Task VerifyAsync(ProjectCompilationService compilation, InstallRuntimeFixture provider, ProjectSourceFixture project)
    {
        var catalog = project.Catalog;
        await using var previews = new ProjectPreviewService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources,
            new(), provider.Roots, provider.Activator);
        var useCases = new ProjectUseCases(compilation, previews, new());
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var authority = new ControlAuthentication(catalog.Instance.InstanceId, false, null, () => new(0, HostSettings.Default), null!, TimeProvider.System);
        app.Use(ControlExceptionBoundary.InvokeAsync); app.Use(authority.InvokeAsync);
        new ProjectControl(authority, useCases, new()).Map(app);
        var frontends = new ExtensionFrontendControl(authority, provider.Activator); frontends.Map(app);
        new ProjectFrontendControl(catalog.Instance.InstanceId, authority, frontends, useCases, new()).Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(address) };
        var discover = Binding(project, "discover"); discover["selection"] = new JsonObject();
        using (var response = await http.PostAsJsonAsync("/control/v1/projects/discover", discover))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("Exists").GetBoolean());
        }
        var unknown = discover.DeepClone().AsObject(); unknown["callerAuthority"] = true;
        using (var response = await http.PostAsJsonAsync("/control/v1/projects/discover", unknown)) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var response = await http.PostAsJsonAsync("/control/v1/projects/preview", discover)) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        http.DefaultRequestHeaders.Add("Origin", "https://unrelated.invalid");
        using (var response = await http.PostAsJsonAsync("/control/v1/projects/discover", discover)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        await PreviewAsync(http, project);
        await SelectAsync(http, project);
        await FrontendAsync(http, provider, project);
        await McpAsync(useCases, project);
        foreach (var action in new[] { "context", "discover", "select", "result", "preview", "page", "release" })
        {
            var request = ControlCliRequest.Parse(["project-" + action, "--address", address, "--input", "-"]);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/control/v1/projects/" + action, request.Path);
        }
        await app.StopAsync();
    }

    private static async Task PreviewAsync(HttpClient http, ProjectSourceFixture project)
    {
        using var response = await http.PostAsJsonAsync("/control/v1/projects/preview", Binding(project, "preview"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = preview.GetProperty("previewId").GetString()!;
        var page = Command("page"); page["previewId"] = id; page["asset"] = "index";
        using (var read = await http.PostAsJsonAsync("/control/v1/projects/page", page))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("e30=", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("contentBase64").GetString());
        }
        page["asset"] = "artifact";
        using (var denied = await http.PostAsJsonAsync("/control/v1/projects/page", page)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        page["asset"] = "../index";
        using (var denied = await http.PostAsJsonAsync("/control/v1/projects/page", page)) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        var release = Command("release"); release["previewId"] = id;
        using (var released = await http.PostAsJsonAsync("/control/v1/projects/release", release)) Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        page["asset"] = "index";
        using (var gone = await http.PostAsJsonAsync("/control/v1/projects/page", page)) Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
        var draft = Binding(project, "preview"); draft["selection"] = new JsonObject { ["previewText"] = "unsaved-draft" };
        using var drafted = await http.PostAsJsonAsync("/control/v1/projects/preview", draft);
        Assert.Equal(HttpStatusCode.OK, drafted.StatusCode);
        page["previewId"] = (await drafted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("previewId").GetString();
        using (var read = await http.PostAsJsonAsync("/control/v1/projects/page", page))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var content = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("contentBase64").GetString()!;
            using var index = JsonDocument.Parse(Convert.FromBase64String(content));
            Assert.Equal("unsaved-draft", index.RootElement.GetProperty("value").GetString());
        }
        var context = Command("context"); context["bindingId"] = project.Binding.ToString("D");
        using var current = await http.PostAsJsonAsync("/control/v1/projects/context", context);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var saved = await current.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, saved.GetProperty("selectionRevision").GetInt64());
        Assert.False(saved.GetProperty("selection").TryGetProperty("previewText", out _));
    }

    private static async Task SelectAsync(HttpClient http, ProjectSourceFixture project)
    {
        var body = Binding(project, "select"); body["operationId"] = Guid.NewGuid().ToString("D"); body["expectedSelectionRevision"] = 3;
        body["selection"] = new JsonObject { ["mount"] = true, ["resourcePath"] = "assets" };
        using (var saved = await http.PostAsJsonAsync("/control/v1/projects/select", body))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var result = await saved.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(result.GetProperty("isNew").GetBoolean());
            Assert.Equal(4, result.GetProperty("revision").GetInt64());
        }
        using (var replay = await http.PostAsJsonAsync("/control/v1/projects/select", body))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.False((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isNew").GetBoolean());
        }
        body["action"] = "result";
        using (var result = await http.PostAsJsonAsync("/control/v1/projects/result", body))
        {
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var saved = await result.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(saved.GetProperty("found").GetBoolean()); Assert.Equal(4, saved.GetProperty("revision").GetInt64());
        }
        body["selection"]!["mount"] = false;
        using (var denied = await http.PostAsJsonAsync("/control/v1/projects/result", body)) Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        body["selection"]!["mount"] = true;
        body["operationId"] = Guid.NewGuid().ToString("D");
        using (var missing = await http.PostAsJsonAsync("/control/v1/projects/result", body))
        {
            Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
            Assert.False((await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("found").GetBoolean());
        }
        body["action"] = "select";
        using var stale = await http.PostAsJsonAsync("/control/v1/projects/select", body); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    private static async Task McpAsync(ProjectUseCases useCases, ProjectSourceFixture project)
    {
        var enabled = true;
        var caller = new AuthenticatedControlRequest(ControlOrigin.Mcp, "fixture-mcp", null, () => new(enabled, true));
        var tools = new McpProjectTools(useCases); var names = tools.List(caller).Select(item => item.Name).ToArray();
        Assert.Contains("project_discover", names); Assert.DoesNotContain("project_select", names);
        var body = Binding(project, "discover"); body["selection"] = new JsonObject();
        var parameters = JsonSerializer.SerializeToElement(new { name = "project_discover", arguments = body });
        var reply = JsonSerializer.SerializeToElement(await tools.CallAsync(caller, parameters));
        Assert.True(reply.GetProperty("structuredContent").GetProperty("Exists").GetBoolean());
        enabled = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.CallAsync(caller, parameters));
        Assert.Empty(tools.List(caller));
        using var schema = new NexusPipeline.Platform.Serialization.ContractReader().Read(JsonSerializer.SerializeToUtf8Bytes(body), "control-project-command.schema.json");
    }

    private static async Task FrontendAsync(HttpClient http, InstallRuntimeFixture provider, ProjectSourceFixture project)
    {
        var activation = provider.Activator.RequirePublishedRuntime("fixture-plugin").ActivationId;
        var route = "/control/v1/extensions/fixture-plugin/frontend/" + activation.ToString("D") + "/project/";
        var body = new JsonObject { ["context"] = new JsonObject { ["surfaceId"] = "automation.binding.editor",
            ["instanceId"] = project.Catalog.Instance.InstanceId.ToString("D"), ["entityId"] = project.Binding.ToString("D") }, ["input"] = new JsonObject() };
        using (var denied = await http.PostAsJsonAsync(route + "context", body)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var grant = await http.PostAsync("/control/v1/extensions/clients", null);
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        var client = await grant.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Add("X-Nexus-Frontend-Client", client.GetProperty("token").GetString());
        using (var read = await http.PostAsJsonAsync(route + "context", body))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var result = await read.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("maas", result.GetProperty("extensionId").GetString()); Assert.Equal(4, result.GetProperty("selectionRevision").GetInt64());
        }
        var operation = Guid.NewGuid().ToString("D");
        body["input"] = new JsonObject { ["expectedBindingRevision"] = 1, ["expectedSelectionRevision"] = 4,
            ["selection"] = new JsonObject { ["mount"] = true, ["resourcePath"] = "assets" } };
        body["operationId"] = operation;
        using (var saved = await http.PostAsJsonAsync(route + "select", body)) Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        body.Remove("operationId"); body["input"]!["operationId"] = operation;
        using (var read = await http.PostAsJsonAsync(route + "result", body))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var result = await read.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(result.GetProperty("found").GetBoolean()); Assert.Equal(5, result.GetProperty("revision").GetInt64());
        }
        body["input"]!["selection"]!["mount"] = false;
        using (var denied = await http.PostAsJsonAsync(route + "result", body)) Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        body["input"] = new JsonObject();
        body["context"]!["instanceId"] = Guid.NewGuid().ToString("D");
        using (var denied = await http.PostAsJsonAsync(route + "context", body)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        body["context"]!["instanceId"] = project.Catalog.Instance.InstanceId.ToString("D");
        body["input"] = new JsonObject { ["expectedBindingRevision"] = 1,
            ["selection"] = new JsonObject { ["previewText"] = "frontend-draft" } };
        using var preview = await http.PostAsJsonAsync(route + "preview", body); Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var id = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("previewId").GetString();
        body["input"] = new JsonObject { ["previewId"] = id, ["asset"] = "index" };
        using (var read = await http.PostAsJsonAsync(route + "page", body))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var content = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("contentBase64").GetString()!;
            using var index = JsonDocument.Parse(Convert.FromBase64String(content));
            Assert.Equal("frontend-draft", index.RootElement.GetProperty("value").GetString());
        }
        var unscoped = Command("page"); unscoped["previewId"] = id; unscoped["asset"] = "index";
        using (var denied = await http.PostAsJsonAsync("/control/v1/projects/page", unscoped)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var retired = await http.PostAsync("/control/v1/extensions/clients/retire", null)) Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);
        using (var denied = await http.PostAsJsonAsync(route + "page", body)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        http.DefaultRequestHeaders.Remove("X-Nexus-Frontend-Client");
        using var next = await http.PostAsync("/control/v1/extensions/clients", null); Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        http.DefaultRequestHeaders.Add("X-Nexus-Frontend-Client", (await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        using (var denied = await http.PostAsJsonAsync(route + "page", body)) Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        http.DefaultRequestHeaders.Remove("X-Nexus-Frontend-Client");
    }

    private static JsonObject Binding(ProjectSourceFixture project, string action)
    {
        var result = Command(action); result["bindingId"] = project.Binding.ToString("D"); result["expectedBindingRevision"] = 1;
        result["extensionId"] = "maas"; return result;
    }
    private static JsonObject Command(string action) => new() { ["format"] = "nexus.control.project-command", ["version"] = 1, ["action"] = action };
}
