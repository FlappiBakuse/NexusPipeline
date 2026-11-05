using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Host.Lifecycle;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceControl(ControlAuthentication authority, HostMaintenanceController maintenance)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/maintenance/restart/{operationId}", async (HttpContext context, string operationId) =>
        {
            _ = authority.Require(context, ControlCapability.MaintenanceWrite); Query(context);
            if (context.Request.ContentLength is not (null or 0) || context.Request.Headers.TransferEncoding.Count != 0)
                throw new ControlRequestException(400, "request_body_invalid");
            return Results.Json(await maintenance.RestartAsync(ControlQueryValues.Id(operationId), context.RequestAborted),
                NotificationUseCases.Json, statusCode: 202);
        });
        app.MapGet("/control/v1/maintenance/operations/{operationId}", (HttpContext context, string operationId) =>
        {
            var request = authority.Require(context, ControlCapability.MaintenanceRead); Query(context);
            var value = maintenance.Read(ControlQueryValues.Id(operationId), request.SubjectId);
            return value is null ? Results.NotFound() : Results.Json(value, NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/maintenance/operations/{operationId}/{action}", async (HttpContext context, string operationId, string action) =>
        {
            var request = authority.Require(context, ControlCapability.MaintenanceWrite); Query(context);
            if (action is not ("start" or "cancel")) throw new ControlRequestException(404, "control_not_found");
            if (context.Request.ContentLength is not (null or 0) || context.Request.Headers.TransferEncoding.Count != 0)
                throw new ControlRequestException(400, "request_body_invalid");
            var id = ControlQueryValues.Id(operationId);
            var value = action == "start" ? await maintenance.StartAsync(id, request.SubjectId, context.RequestAborted)
                : maintenance.Cancel(id, request.SubjectId);
            return value is null ? Results.NotFound() : Results.Json(value, NotificationUseCases.Json,
                statusCode: action == "start" ? 202 : 200);
        });
    }
    private static void Query(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
}
