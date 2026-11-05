using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Host.Control.Http;

internal sealed class SecretsControl(ISecretFieldStore store)
{
    private const string Route = "/control/v1/secrets/{extensionId}/{subjectId}/{field}";
    private static readonly JsonSerializerOptions StatusOptions = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string[] Capabilities { get; } = ["secrets.status", "secrets.read", "secrets.write"];

    public void Map(WebApplication app)
    {
        app.MapGet(Route, (Delegate)StatusAsync);
        app.MapPost(Route + "/read", (Delegate)ReadAsync);
        app.MapPost(Route + "/change", (Delegate)ChangeAsync);
    }

    private async Task<IResult> StatusAsync(HttpContext context, string extensionId, string subjectId, string field)
    {
        if (!LocalControlAccess.IsAllowed(context, LocalControlCapability.SecretStatus)) return ControlErrors.Result(403, "access_denied");
        try
        {
            var status = await store.GetStateAsync(Key(context, extensionId, subjectId, field), context.RequestAborted);
            return Status(status);
        }
        catch (Exception error) when (!context.RequestAborted.IsCancellationRequested) { return ControlErrors.Secret(error, true); }
    }

    private async Task<IResult> ChangeAsync(HttpContext context, string extensionId, string subjectId, string field)
    {
        if (!LocalControlAccess.IsAllowed(context, LocalControlCapability.SecretWrite)) return ControlErrors.Result(403, "access_denied");
        Guid? operationId = null;
        try
        {
            var key = Key(context, extensionId, subjectId, field);
            var request = context.Request;
            operationId = Uuid(Single(request, "Nexus-Operation-Id"));
            var revisionText = Single(request, "Nexus-Expected-Revision");
            if (!long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
                || revision.ToString(CultureInfo.InvariantCulture) != revisionText) throw new ArgumentException("secret_revision_invalid");
            var disposition = Single(request, "Nexus-Secret-Disposition") switch
            {
                "keep" => SecretDisposition.Keep, "set" => SecretDisposition.Set, "clear" => SecretDisposition.Clear,
                _ => throw new ArgumentException("secret_disposition_invalid")
            };
            if (disposition == SecretDisposition.Set && !IsUtf8Text(request.ContentType)) return ControlErrors.Result(415, "secret_media_type_invalid", operationId);
            using var body = await SecretRequestBody.ReadAsync(context, disposition == SecretDisposition.Set ? SecretRequestBody.MaximumBytes : 0);
            using var value = disposition == SecretDisposition.Set ? body.Decode() : null;
            var status = await store.ApplyAsync(operationId.Value, key, revision, disposition, value, context.RequestAborted);
            return Status(status);
        }
        catch (SecretBodyException error) { return ControlErrors.Result(error.Status, error.Message, operationId); }
        catch (Exception error) when (!context.RequestAborted.IsCancellationRequested) { return ControlErrors.Secret(error, false, operationId); }
    }

    private async Task ReadAsync(HttpContext context, string extensionId, string subjectId, string field)
    {
        if (!LocalControlAccess.IsAllowed(context, LocalControlCapability.SecretRead))
        { await ControlErrors.Result(403, "access_denied").ExecuteAsync(context); return; }
        byte[]? bytes = null;
        try
        {
            var key = Key(context, extensionId, subjectId, field);
            using var body = await SecretRequestBody.ReadAsync(context, 0);
            using var value = await store.ReadAsync(key, context.RequestAborted);
            if (!LocalControlAccess.IsAllowed(context, LocalControlCapability.SecretRead))
            { await ControlErrors.Result(403, "access_denied").ExecuteAsync(context); return; }
            value.Use(characters =>
            {
                bytes = new byte[Utf8.GetByteCount(characters)];
                Utf8.GetBytes(characters, bytes);
            });
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength = bytes!.Length;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        catch (SecretBodyException error) { await ControlErrors.Result(error.Status, error.Message).ExecuteAsync(context); }
        catch (Exception error) when (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        { await ControlErrors.Secret(error, true).ExecuteAsync(context); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static IResult Status(SecretFieldStatus value) => Results.Json(new
    {
        format = "nexus.secret.status", version = 1, revision = value.Revision, secretId = value.SecretId,
        isConfigured = value.IsConfigured, isReadable = value.IsReadable
    }, StatusOptions);

    private static SecretFieldKey Key(HttpContext context, string extensionId, string subjectId, string field)
    {
        if (context.Request.QueryString.HasValue) throw new ArgumentException("secret_query_invalid");
        return new(extensionId, Uuid(subjectId), field);
    }

    private static Guid Uuid(string text) => Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty
        && value.ToString("D") == text ? value : throw new ArgumentException("secret_uuid_invalid");

    private static string Single(HttpRequest request, string name) => request.Headers[name] is { Count: 1 } values
        && values[0] is { Length: > 0 } value ? value : throw new ArgumentException("secret_header_invalid");

    private static bool IsUtf8Text(string? text) => MediaTypeHeaderValue.TryParse(text, out var value)
        && value.MediaType?.Equals("text/plain", StringComparison.OrdinalIgnoreCase) == true && value.Parameters.Count == 1
        && value.CharSet?.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase) == true;
}
