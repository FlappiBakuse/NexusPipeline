using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class HostSecretsControl(ControlAuthentication authority, SecretFieldService secrets, Guid instanceId)
{
    private const string Route = "/control/v1/local/secrets/{field}";
    internal void Map(WebApplication app)
    {
        app.MapGet(Route, async (HttpContext context, string field) =>
        {
            Require(context, ControlCapability.SecretStatus);
            return Results.Json(await secrets.GetStateAsync(Key(field), context.RequestAborted));
        });
        app.MapPost(Route + "/read", (Delegate)ReadAsync);
        app.MapPost(Route + "/change", (Delegate)ChangeAsync);
        app.MapGet(Route + "/operations/{operationId}", (HttpContext context, string field, string operationId) =>
        {
            Require(context, ControlCapability.SecretStatus);
            var operation = secrets.ReadHostOperation(ControlQueryValues.Id(operationId), field);
            return operation is null ? Results.NotFound() : Results.Json(operation);
        });
    }

    private async Task ReadAsync(HttpContext context, string field)
    {
        byte[]? bytes = null;
        try
        {
            Require(context, ControlCapability.SecretRead);
            _ = Key(field);
            using var body = await SecretRequestBody.ReadAsync(context, 0);
            using var value = secrets.ReadLocalHost(field);
            value.Use(characters =>
            {
                bytes = new byte[Encoding.UTF8.GetByteCount(characters)];
                Encoding.UTF8.GetBytes(characters, bytes);
            });
            Require(context, ControlCapability.SecretRead);
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength = bytes!.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        catch (UnauthorizedAccessException) when (!context.Response.HasStarted)
        { await ControlErrors.Result(403, "control_local_origin_required").ExecuteAsync(context); }
        catch (SecretBodyException error) { await ControlErrors.Result(error.Status, "secret_body_invalid").ExecuteAsync(context); }
        catch (Exception error) when (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        { await ControlErrors.Secret(error, true).ExecuteAsync(context); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<IResult> ChangeAsync(HttpContext context, string field)
    {
        Guid? operation = null;
        try
        {
            Require(context, ControlCapability.SecretWrite);
            if (field != "proxy-password") throw new UnauthorizedAccessException("control_token_confirmation_required");
            operation = ControlQueryValues.Id(Header(context, "Nexus-Operation-Id"));
            var raw = Header(context, "Nexus-Expected-Revision");
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0 ||
                revision.ToString(CultureInfo.InvariantCulture) != raw) throw new ArgumentException("secret_revision_invalid");
            var disposition = Header(context, "Nexus-Secret-Disposition") switch
            {
                "keep" => SecretDisposition.Keep, "set" => SecretDisposition.Set, "clear" => SecretDisposition.Clear,
                _ => throw new ArgumentException("secret_disposition_invalid")
            };
            if (disposition == SecretDisposition.Set && (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media) ||
                !string.Equals(media.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase) || media.Parameters.Count != 1 ||
                !string.Equals(media.CharSet?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
                return ControlErrors.Result(415, "secret_media_type_invalid", operation);
            using var body = await SecretRequestBody.ReadAsync(context, disposition == SecretDisposition.Set ? SecretRequestBody.MaximumBytes : 0);
            using var value = disposition == SecretDisposition.Set ? body.Decode() : null;
            var status = await secrets.ApplyAsync(operation.Value, Key(field), revision, disposition, value, CancellationToken.None);
            return Results.Json(new { operationId = operation, revision = status.Revision,
                isConfigured = status.IsConfigured, isReadable = status.IsReadable });
        }
        catch (UnauthorizedAccessException) { return ControlErrors.Result(403, "control_local_origin_required", operation); }
        catch (SecretBodyException error) { return ControlErrors.Result(error.Status, "secret_body_invalid", operation); }
        catch (Exception error) when (!context.RequestAborted.IsCancellationRequested) { return ControlErrors.Secret(error, false, operation); }
    }

    private void Require(HttpContext context, ControlCapability capability)
    {
        var request = authority.Require(context, capability);
        if (request.Origin != ControlOrigin.Local || context.Request.QueryString.HasValue)
            throw new UnauthorizedAccessException("control_local_origin_required");
    }
    private SecretFieldKey Key(string field) => field switch
    {
        "proxy-password" => SecretFieldKey.HostProxyPassword(instanceId),
        "control-bearer" => SecretFieldKey.HostControlBearer(instanceId),
        _ => throw new ArgumentException("host_secret_field_invalid")
    };
    private static string Header(HttpContext context, string name) =>
        context.Request.Headers[name] is { Count: 1 } values && values[0] is { Length: > 0 } value ? value :
            throw new ArgumentException("secret_header_invalid");
}
