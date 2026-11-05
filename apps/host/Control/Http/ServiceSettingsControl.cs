using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Settings;

namespace NexusPipeline.Host.Control.Http;

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
