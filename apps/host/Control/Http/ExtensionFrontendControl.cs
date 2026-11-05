using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionFrontendControl(ControlAuthentication authority, ExtensionActivator activator)
{
    internal FrontendClients Clients { get; } = new(TimeProvider.System);

    internal void Map(WebApplication app)
    {
        Clients.Map(app, authority);
        new ExtensionUploadControl(authority, this).Map(app);
        app.MapGet("/control/v1/extensions/frontend", async (HttpContext context) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead); Query(context);
            var values = new List<object>();
            foreach (var item in activator.ReadPublishedScopes())
            {
                var runtime = activator.RequirePublishedRuntime(item.ExtensionId);
                if (runtime.Frontend is { } frontend)
                    values.Add(new { item.ActivationId, manifest = await frontend.ReadManifestAsync(context.RequestAborted) });
            }
            caller.Require(ControlCapability.ExtensionsRead);
            return Results.Json(new { connectionKind = caller.Origin == ControlOrigin.Local ? "local" : "remote", extensions = values },
                NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/extensions/{extensionId}/frontend/{activationId}/assets/{asset}",
            async (HttpContext context, string extensionId, string activationId, string asset) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead); Query(context);
            var frontend = Current(extensionId, activationId);
            var bytes = await frontend.ReadAssetAsync(asset, context.RequestAborted);
            caller.Require(ControlCapability.ExtensionsRead);
            return Results.Bytes(bytes, asset == "entry" ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8");
        });
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
        });
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
        });
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
