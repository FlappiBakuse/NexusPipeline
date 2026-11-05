using System.Buffers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using NexusPipeline.Core.Control;

namespace NexusPipeline.Host.Control.Http;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ExtensionUploadControl(ControlAuthentication authority, ExtensionFrontendControl frontends)
{
    private const int MaximumBytes = 8388608;
    private readonly SemaphoreSlim _requests = new(2, 2);

    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/extensions/{extensionId}/frontend/{activationId}/upload/{operation}", UploadAsync);
    }

    private async Task<IResult> UploadAsync(HttpContext context, string extensionId, string activationId, string operation)
    {
        var caller = authority.Require(context, ControlCapability.ExtensionsRead);
        var client = frontends.Clients.Read(context, caller);
        caller.Require(ControlCapability.SettingsWrite);
        if (context.Request.QueryString.HasValue || context.Request.Headers.ContentEncoding.Count != 0)
            throw new ControlRequestException(400, "request_upload_invalid");
        var frontend = frontends.Current(extensionId, activationId);
        if (await frontend.CapabilityAsync(operation, context.RequestAborted) != ControlCapability.SettingsWrite)
            throw new ControlRequestException(400, "request_upload_operation_invalid");
        var contentType = context.Request.ContentType;
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new ControlRequestException(415, "request_media_type_invalid");
        var operationHeader = context.Request.Headers["X-Nexus-Operation-Id"];
        if (operationHeader.Count != 1 || !Guid.TryParseExact(operationHeader[0], "D", out var operationId) ||
            operationId == Guid.Empty || operationHeader[0] != operationId.ToString("D"))
            throw new ControlRequestException(400, "request_identity_invalid");
        var nameHeader = context.Request.Headers["X-Nexus-File-Name"];
        if (nameHeader.Count != 1 || nameHeader[0] is not { Length: >= 1 and <= 2048 })
            throw new ControlRequestException(400, "request_upload_name_invalid");
        var name = Uri.UnescapeDataString(nameHeader[0]!);
        if (name.Length is < 1 or > 256 || name.Any(value => char.IsControl(value) || value is '/' or '\\'))
            throw new ControlRequestException(400, "request_upload_name_invalid");
        if (context.Request.ContentLength is < 1 or > MaximumBytes) throw new ControlRequestException(413, "request_too_large");
        if (!await _requests.WaitAsync(0, context.RequestAborted)) throw new ControlRequestException(429, "request_upload_busy");
        byte[]? bytes = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is null || limit.IsReadOnly) throw new IOException("request_upload_limit_unavailable");
            limit.MaxRequestBodySize = MaximumBytes;
            bytes = ArrayPool<byte>.Shared.Rent(MaximumBytes + 1); var count = 0;
            while (count <= MaximumBytes)
            {
                var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count, MaximumBytes + 1 - count), deadline.Token);
                if (read == 0) break;
                count += read;
            }
            if (count is < 1 or > MaximumBytes) throw new ControlRequestException(413, "request_too_large");
            var result = await frontend.InvokeAsync(operation, operationId, caller.SubjectId,
                caller.Origin == ControlOrigin.Local, bytes.AsMemory(0, count),
                () => { caller.Require(ControlCapability.SettingsWrite); client?.RequireCurrent(); },
                deadline.Token, contentType, name, client?.ClientId, client?.Lifetime ?? default);
            if (result.ContentType != "application/json") throw new IOException("extension_frontend_upload_response_invalid");
            return Results.Bytes(result.Content.ToArray(), result.ContentType);
        }
        finally
        {
            if (bytes is not null) { CryptographicOperations.ZeroMemory(bytes); ArrayPool<byte>.Shared.Return(bytes); }
            _requests.Release();
        }
    }
}
