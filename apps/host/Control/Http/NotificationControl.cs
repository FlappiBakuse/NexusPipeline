using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class NotificationControl(ControlAuthentication authority, NotificationUseCases useCases, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/notifications", (HttpContext context) =>
        {
            NoQuery(context);
            return Results.Json(useCases.Read(authority.Require(context, ControlCapability.NotificationsRead)),
                NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/notifications/{taskId}/credentials", async (HttpContext context, string taskId) =>
        {
            NoQuery(context);
            if (!Guid.TryParseExact(taskId, "D", out var task) || task == Guid.Empty || taskId != task.ToString("D"))
                throw new ControlRequestException(400, "request_identity_invalid");
            return Results.Json(await useCases.CredentialsAsync(
                authority.Require(context, ControlCapability.NotificationsRead), task), NotificationUseCases.Json);
        });
        foreach (var action in new[] { "save", "delete", "reorder", "limit", "test" })
            MapAction(app, action);
    }

    private void MapAction(WebApplication app, string action) =>
        app.MapPost("/control/v1/notifications/" + action, async (HttpContext context) =>
        {
            NoQuery(context);
            var request = authority.Require(context, NotificationUseCases.Capability(action));
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-notification-command.schema.json");
            using var command = NotificationControlCommand.Read(body.Root);
            if (command.Action != action) throw new ControlRequestException(400, "notification_action_mismatch");
            var result = await useCases.ExecuteAsync(request, command);
            if (action == "test")
                return Results.Json(result, NotificationUseCases.Json, statusCode: StatusCodes.Status202Accepted);
            return Results.Json(result, NotificationUseCases.Json);
        });

    private static void NoQuery(HttpContext context)
    {
        if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid");
    }
}
