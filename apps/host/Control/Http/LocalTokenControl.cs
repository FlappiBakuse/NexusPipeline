using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class LocalTokenControl(ControlAuthentication authority, ControlTokenService tokens)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/local/control-token/change", (Delegate)ChangeAsync);
        app.MapGet("/control/v1/local/control-token/operations/{operationId}", (Delegate)ReadOperation);
        app.MapPost("/control/v1/local/control-token/{operationId}/cancel", (Delegate)CancelAsync);
    }

    private IResult ReadOperation(HttpContext context, string operationId)
    {
        var identity = authority.Require(context, ControlCapability.SecretStatus);
        if (identity.Origin != ControlOrigin.Local || context.Request.QueryString.HasValue)
            throw new UnauthorizedAccessException("control_local_origin_required");
        var value = tokens.ReadOperation(Uuid(operationId));
        return value is null ? Results.NotFound() : Results.Json(value);
    }

    private async Task<IResult> ChangeAsync(HttpContext context)
    {
        Guid? operation = null;
        try
        {
            var identity = authority.Require(context, ControlCapability.SecretWrite);
            if (identity.Origin != ControlOrigin.Local || context.Request.QueryString.HasValue)
                throw new UnauthorizedAccessException("control_local_origin_required");
            operation = Uuid(Header(context.Request, "Nexus-Operation-Id"));
            var revisionText = Header(context.Request, "Nexus-Expected-Revision");
            if (!long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
                revision < 0 || revision.ToString(CultureInfo.InvariantCulture) != revisionText)
                throw new ArgumentException("control_token_revision_invalid");
            var disposition = Header(context.Request, "Nexus-Secret-Disposition") switch
            {
                "set" => SecretDisposition.Set, "clear" => SecretDisposition.Clear,
                _ => throw new ArgumentException("control_token_change_invalid")
            };
            if (context.Request.Headers.ContentEncoding.Count != 0 || disposition == SecretDisposition.Set &&
                (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media) ||
                 !string.Equals(media.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase) ||
                 media.Parameters.Count != 1 || !string.Equals(media.CharSet?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
                return ControlErrors.Result(415, "control_token_media_type_invalid", operation);
            using var body = await SecretRequestBody.ReadAsync(context, disposition == SecretDisposition.Set ? 256 : 0);
            using var value = disposition == SecretDisposition.Set ? body.Decode() : null;
            var work = tokens.ChangeAsync(operation.Value, revision, disposition, value);
            var status = await work.WaitAsync(context.RequestAborted);
            identity.Require(ControlCapability.SecretWrite);
            return Results.Json(new
            {
                operationId = operation, revision = status.Revision,
                isConfigured = status.IsConfigured, isReadable = status.IsReadable
            });
        }
        catch (SecretBodyException error) { return ControlErrors.Result(error.Status, "control_token_body_invalid", operation); }
        catch (UnauthorizedAccessException) { return ControlErrors.Result(403, "access_denied", operation); }
        catch (Exception error) when (!context.RequestAborted.IsCancellationRequested)
        { return ControlErrors.Secret(error, false, operation); }
    }

    private async Task<IResult> CancelAsync(HttpContext context, string operationId)
    {
        try
        {
            var identity = authority.Require(context, ControlCapability.SecretWrite);
            if (identity.Origin != ControlOrigin.Local || context.Request.QueryString.HasValue)
                throw new UnauthorizedAccessException("control_local_origin_required");
            var operation = Uuid(operationId);
            using var body = await SecretRequestBody.ReadAsync(context, 0);
            return Results.Json(new { operationId = operation, cancelled = tokens.Cancel(operation) });
        }
        catch (UnauthorizedAccessException) { return ControlErrors.Result(403, "access_denied"); }
        catch (SecretBodyException error) { return ControlErrors.Result(error.Status, "control_token_body_invalid"); }
        catch (ArgumentException) { return ControlErrors.Result(400, "request_identity_invalid"); }
    }

    private static string Header(HttpRequest request, string name) =>
        request.Headers[name] is { Count: 1 } values && values[0] is { Length: > 0 } value ? value :
            throw new ArgumentException("control_header_invalid");
    private static Guid Uuid(string value) => Guid.TryParseExact(value, "D", out var id) &&
        id != Guid.Empty && id.ToString("D") == value ? id : throw new ArgumentException("control_identity_invalid");
}
