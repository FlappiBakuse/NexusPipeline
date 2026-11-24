using NexusPipeline.Core.Control;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Host.Control.Settings;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Settings;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class HostSettingsControl(ControlAuthentication authority, SettingsUseCases settings, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/settings", (HttpContext context) =>
        {
            NoQuery(context);
            return Results.Json(settings.Read(authority.Require(context, ControlCapability.SettingsRead)), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/settings", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.SettingsWrite); NoQuery(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-host-settings.schema.json");
            var saved = await settings.SaveAsync(request, body.Root, context.RequestAborted);
            app.Logger.LogInformation("Host settings saved");
            return Results.Json(saved, NotificationUseCases.Json);
        });
    }
    private static void NoQuery(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
}

[SupportedOSPlatform("windows")]
internal sealed class InterfaceControl(ControlAuthentication authority, InterfacePreferences preferences, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/settings/interface", (HttpContext context) =>
        {
            authority.Require(context, ControlCapability.SettingsRead); NoQuery(context);
            return Results.Json(preferences.Read(), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/settings/interface/change", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.SettingsWrite); NoQuery(context);
            using var body = await ControlRequestBody.ReadAsync(context, 16384);
            body.RequireSchema(contracts, "control-interface-change.schema.json");
            var root = body.Root; var values = root.GetProperty("values");
            var operation = ControlRequestBody.Uuid(root.GetProperty("operationId"));
            try
            {
                var saved = preferences.Save(operation, request.SubjectId, root.GetProperty("expectedRevision").GetInt64(),
                    new(values.GetProperty("theme").GetString()!,
                        Strings("dashboardCards"), Strings("pinnedSettings"), Strings("collapsedSettings"),
                        values.GetProperty("locale").GetString()!,
                        values.TryGetProperty("dashboardAvailableCards", out _) ? Strings("dashboardAvailableCards") : null));
                return Results.Json(saved, NotificationUseCases.Json);
            }
            catch (InterfaceConflictException) { return ControlErrors.Result(409, "interface_revision_conflict", operation); }
            string[] Strings(string name) => values.GetProperty(name).EnumerateArray().Select(value => value.GetString()!).ToArray();
        });
    }
    private static void NoQuery(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
}

[SupportedOSPlatform("windows")]
internal sealed class ServiceSettingsControl(ControlAuthentication authority, HostSettingsService service, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/settings/service", (HttpContext context) =>
        {
            _ = authority.Require(context, ControlCapability.SettingsRead); NoQuery(context);
            var saved = service.Read().Saved;
            return Results.Json(new { saved.Revision, service = saved.Values.Service });
        });
        app.MapPost("/control/v1/settings/service", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.SettingsWrite); NoQuery(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-service-settings.schema.json");
            var root = body.Root;
            var operation = ControlRequestBody.Uuid(root.GetProperty("operationId"));
            var revision = root.GetProperty("expectedRevision").GetInt64();
            var item = root.GetProperty("service");
            var candidate = new ServicePreferences(item.GetProperty("webPort").GetInt32(),
                item.GetProperty("allowRemoteAccess").GetBoolean(), item.GetProperty("mcpEnabled").GetBoolean(),
                item.GetProperty("mcpWriteEnabled").GetBoolean(), item.GetProperty("mcpPort").GetInt32());
            request.Require(ControlCapability.SettingsWrite);
            var saved = service.SaveService(operation, request.SubjectId, revision, candidate, DateTimeOffset.UtcNow);
            return Results.Json(new { saved.OperationId, saved.IsNew, saved = new
                { saved.Saved.Revision, service = saved.Saved.Values.Service } });
        });
    }

    private static void NoQuery(HttpContext context)
    {
        if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid");
    }
}
