using NexusPipeline.Core.Control;
using NexusPipeline.Core.Operations;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Host.Control.Runtime;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        }).WithMetadata(ControlRequestLimits.ResponseStream);
    }

    private static void NoQuery(HttpContext context)
    {
        if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ControlQueries(ControlAuthentication authority, IOperationQueries operations,
    Guid instanceId, Func<string?> desktopVerificationKey, IReadOnlySet<ControlCapability> availableCapabilities)
{
    private readonly HashSet<ControlCapability> _available = availableCapabilities.ToHashSet();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/capabilities", (HttpContext context) =>
        {
            var identity = authority.Require(context, ControlCapability.CapabilitiesRead);
            return Results.Json(new
            {
                instanceId, hostVersion = "0.18.0", sdkVersion = "0.1.0",
                capabilities = identity.Visible().Where(value => _available.Contains(value.Id)).Select(value => value.Name).ToArray(),
                desktopVerificationKey = desktopVerificationKey()
            }, Json);
        });
        app.MapGet("/control/v1/operations/{operationId}", (HttpContext context, string operationId) =>
        {
            var identity = authority.Require(context, ControlCapability.OperationsRead);
            if (context.Request.QueryString.HasValue || !Guid.TryParseExact(operationId, "D", out var id) ||
                id == Guid.Empty || id.ToString("D") != operationId) return ControlErrors.Result(400, "request_identity_invalid");
            var state = operations.Read(id, identity.SubjectId);
            return state is null ? Results.NotFound() : Results.Json(state);
        });
    }
}
