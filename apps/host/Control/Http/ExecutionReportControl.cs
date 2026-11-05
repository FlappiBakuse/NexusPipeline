using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionReportControl(ControlAuthentication authority, ExecutionLogReader reader)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/records/{runId}/attempts/{attemptId}/report", (HttpContext context, string runId, string attemptId) =>
        {
            _ = authority.Require(context, ControlCapability.RecordsRead);
            new ControlQueryValues(context.Request.Query).Validate();
            return Results.Json(reader.ReadReport(ControlQueryValues.Id(runId), ControlQueryValues.Id(attemptId)), NotificationUseCases.Json);
        });
    }
}
