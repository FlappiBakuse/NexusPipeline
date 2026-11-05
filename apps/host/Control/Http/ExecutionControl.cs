using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Execution;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionControl(ControlAuthentication authority, ExecutionUseCases useCases, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/execution/preview", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.ExecutionPreview);
            if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid");
            using var body = await ControlRequestBody.ReadAsync(context, 4096);
            body.RequireSchema(contracts, "control-execution-selection.schema.json");
            return Results.Json(useCases.Preview(request, ExecutionControlCommand.Selection(body.Root)),
                NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/execution/start/{operationId}", async (HttpContext context, string operationId) =>
        {
            var request = authority.Require(context, ControlCapability.ExecutionStart);
            using var body = await ControlRequestBody.ReadAsync(context, 4096);
            body.RequireSchema(contracts, "control-execution-selection.schema.json");
            var result = useCases.Start(request, ControlQueryValues.Id(operationId), ExecutionControlCommand.Selection(body.Root));
            return Results.Json(result, NotificationUseCases.Json, statusCode: 202);
        });
        app.MapPost("/control/v1/execution/{startOperationId}/cancel/{operationId}",
            async (HttpContext context, string startOperationId, string operationId) =>
        {
            var request = authority.Require(context, ControlCapability.ExecutionCancel);
            using var body = await ControlRequestBody.ReadAsync(context, 1024);
            body.RequireSchema(contracts, "control-execution-cancellation.schema.json");
            var root = body.Root;
            ControlRequestBody.RequireMembers(root, "format", "version", "runId", "attemptId");
            var result = useCases.Cancel(request, ControlQueryValues.Id(operationId), ControlQueryValues.Id(startOperationId),
                ControlRequestBody.Uuid(root.GetProperty("runId")), ControlRequestBody.Uuid(root.GetProperty("attemptId")));
            return Results.Json(result, NotificationUseCases.Json, statusCode: 202);
        });
    }
}
