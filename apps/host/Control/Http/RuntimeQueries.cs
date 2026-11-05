using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Host.Control.Runtime;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class RuntimeQueries(ControlAuthentication authority, RuntimeUseCases useCases)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/doctor", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.DoctorRead); NoQuery(context);
            return Results.Json(useCases.Doctor(request, context.RequestAborted), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/status", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.StatusRead); NoQuery(context);
            return Results.Json(useCases.Status(request, context.RequestAborted, context.Connection.LocalPort), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/support", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.DoctorRead); NoQuery(context);
            return Results.File(useCases.Support(request, context.RequestAborted), "application/zip", "nexus-support.zip");
        });
    }

    private static void NoQuery(HttpContext context)
    {
        if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid");
    }
}
