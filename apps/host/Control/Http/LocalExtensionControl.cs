using NexusPipeline.Core.Control;
using NexusPipeline.Platform.Storage.Extensions;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class LocalExtensionControl(ControlAuthentication authority, ExtensionPackageService packages, Func<bool> pending)
{
    private const string Route = "/control/v1/extensions/local";

    internal void Map(WebApplication app)
    {
        app.MapGet(Route, (HttpContext context) =>
        {
            _ = RequireLocal(context, false);
            return Results.Json(packages.ReadLocalIntake());
        });
        app.MapPost(Route + "/scan", async (HttpContext context) =>
        {
            _ = RequireLocal(context, false);
            using var body = await ControlRequestBody.ReadAsync(context);
            if (body.Root.ValueKind != JsonValueKind.Object || body.Root.EnumerateObject().Any())
                throw new ControlRequestException(400, "request_body_invalid");
            return Results.Json(await packages.ScanLocalIntakeAsync().WaitAsync(context.RequestAborted));
        });
        app.MapPost(Route + "/enable", async (HttpContext context) =>
        {
            var request = RequireLocal(context, true);
            using var body = await ControlRequestBody.ReadAsync(context);
            if (body.Root.ValueKind != JsonValueKind.Object || body.Root.EnumerateObject().Count() != 2 ||
                !body.Root.TryGetProperty("candidateId", out var candidate) || candidate.ValueKind != JsonValueKind.String ||
                !body.Root.TryGetProperty("operationId", out var operation))
                throw new ControlRequestException(400, "request_body_invalid");
            request.Require(ControlCapability.SettingsWrite);
            try
            {
                return Results.Json(await packages.EnableLocalAsync(candidate.GetString()!,
                    ControlRequestBody.Uuid(operation), request.SubjectId).WaitAsync(context.RequestAborted));
            }
            catch (IOException error) when (error.Message.StartsWith("extension_local_", StringComparison.Ordinal))
            { throw new ControlRequestException(409, error.Message); }
        });
    }

    private AuthenticatedControlRequest RequireLocal(HttpContext context, bool writing)
    {
        var caller = authority.Require(context, writing ? ControlCapability.ExtensionsWrite : ControlCapability.ExtensionsRead);
        if (caller.Origin != ControlOrigin.Local) throw new UnauthorizedAccessException();
        if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid");
        if (pending() && !HttpMethods.IsGet(context.Request.Method))
            throw new ControlRequestException(409, "initial_extensions_pending");
        return caller;
    }
}
