using Microsoft.AspNetCore.Http.Features;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

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
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is null || limit.IsReadOnly) throw new IOException("request_upload_limit_unavailable");
            limit.MaxRequestBodySize = MaximumBytes;
            using var body = await ReadUploadAsync(context.Request, deadline.Token);
            var result = await frontend.InvokeAsync(operation, operationId, caller.SubjectId,
                caller.Origin == ControlOrigin.Local, body.Content,
                () => { caller.Require(ControlCapability.SettingsWrite); client?.RequireCurrent(); },
                deadline.Token, contentType, name, client?.ClientId, client?.Lifetime ?? default);
            if (result.ContentType != "application/json") throw new IOException("extension_frontend_upload_response_invalid");
            return Results.Bytes(result.Content.ToArray(), result.ContentType);
        }
        finally { _requests.Release(); }
    }

    internal static async Task<PooledControlBody> ReadUploadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var body = await ControlRequestBody.ReadBytesAsync(request, MaximumBytes, cancellationToken);
        if (!body.Content.IsEmpty) return body;
        body.Dispose();
        throw new ControlRequestException(413, "request_too_large");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ExtensionFrontendControl(ControlAuthentication authority, ExtensionActivator activator)
{
    internal FrontendClients Clients { get; } = new(TimeProvider.System, authority.InstanceId);

    internal void Map(WebApplication app)
    {
        Clients.Map(app, authority);
        new ExtensionUploadControl(authority, this).Map(app);
        app.MapGet("/control/v1/extensions/frontend", async (HttpContext context) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead); Query(context);
            var scopes = activator.ReadPublishedScopes();
            var values = new object?[scopes.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, scopes.Count), new ParallelOptions
                { MaxDegreeOfParallelism = 4, CancellationToken = context.RequestAborted }, async (index, cancellation) =>
            {
                var item = scopes[index];
                var runtime = activator.RequirePublishedRuntime(item.ExtensionId);
                if (runtime.Frontend is { } frontend)
                    values[index] = new { item.ActivationId, manifest = await frontend.ReadManifestAsync(cancellation) };
            });
            caller.Require(ControlCapability.ExtensionsRead);
            return Results.Json(new { connectionKind = caller.Origin == ControlOrigin.Local ? "local" : "remote", extensions = values.Where(value => value is not null) },
                NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/extensions/{extensionId}/frontend/{activationId}/assets",
            async (HttpContext context, string extensionId, string activationId) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead); Query(context);
            var assets = await Current(extensionId, activationId).ReadAssetsAsync(context.RequestAborted);
            caller.Require(ControlCapability.ExtensionsRead);
            return Results.Json(assets, NotificationUseCases.Json);
        }).WithMetadata(ControlRequestLimits.ResponseStream);
        app.MapGet("/control/v1/extensions/{extensionId}/frontend/{activationId}/assets/{asset}",
            async (HttpContext context, string extensionId, string activationId, string asset) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead); Query(context);
            var frontend = Current(extensionId, activationId);
            var bytes = await frontend.ReadAssetAsync(asset, context.RequestAborted);
            caller.Require(ControlCapability.ExtensionsRead);
            return Results.Bytes(bytes, asset == "entry" ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8");
        }).WithMetadata(ControlRequestLimits.ResponseStream);
        app.MapGet("/control/v1/extensions/{extensionId}/frontend/{activationId}/identity",
            async (HttpContext context, string extensionId, string activationId) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead); Query(context);
            _ = await Current(extensionId, activationId).ReadManifestAsync(context.RequestAborted);
            caller.Require(ControlCapability.ExtensionsRead);
            return Results.Json(new { activationId });
        });
        app.MapGet("/control/v1/extensions/{extensionId}/frontend/{activationId}/images/{operation}/{assetId}",
            async (HttpContext context, string extensionId, string activationId, string operation, string assetId) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead); Query(context);
            var frontend = Current(extensionId, activationId);
            caller.Require(ControlCapability.SettingsRead);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await frontend.ReadImageAsync(operation, assetId, caller.SubjectId,
                caller.Origin == ControlOrigin.Local, () => caller.Require(ControlCapability.SettingsRead), deadline.Token);
            return Results.Bytes(result.Content.ToArray(), result.ContentType);
        }).WithMetadata(ControlRequestLimits.ResponseStream);
        app.MapPost("/control/v1/extensions/{extensionId}/frontend/{activationId}/invoke/{operation}",
            async (HttpContext context, string extensionId, string activationId, string operation) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead);
            var client = Clients.Read(context, caller);
            var frontend = Current(extensionId, activationId);
            var capability = await frontend.CapabilityAsync(operation, context.RequestAborted);
            caller.Require(capability); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            ControlRequestBody.RequireMembers(body.Root, "operationId", "input");
            if (!body.Root.TryGetProperty("input", out var input)) throw new ControlRequestException(400, "request_shape_invalid");
            var operationId = body.Root.TryGetProperty("operationId", out var id) && id.ValueKind != JsonValueKind.Null ?
                ControlRequestBody.Uuid(id) : (Guid?)null;
            var payload = JsonSerializer.SerializeToUtf8Bytes(input);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(capability == ControlCapability.Desktop ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(30));
                var result = await frontend.InvokeAsync(operation, operationId, caller.SubjectId,
                    caller.Origin == ControlOrigin.Local, payload,
                    () => { caller.Require(capability); client?.RequireCurrent(); }, deadline.Token,
                    clientId: client?.ClientId, clientLifetime: client?.Lifetime ?? default);
                return Results.Bytes(result.Content.ToArray(), result.ContentType);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }).WithMetadata(ControlRequestLimits.ResponseStream);
    }

    internal InstalledFrontend Current(string extensionId, string activationId)
    {
        var runtime = activator.RequirePublishedRuntime(extensionId);
        if (runtime.ActivationId != ControlQueryValues.Id(activationId)) throw new IOException("extension_frontend_activation_changed");
        return runtime.Frontend ?? throw new IOException("extension_frontend_unavailable");
    }

    internal static string Identity(ExtensionActivator activator) => Convert.ToHexStringLower(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join('\n', activator.ReadPublishedScopes()
            .OrderBy(item => item.ExtensionId, StringComparer.Ordinal)
            .Select(item => item.ExtensionId + ":" + item.ActivationId.ToString("D"))))));

    private static void Query(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
}
