using NexusPipeline.Core.Control;
using NexusPipeline.Core.Desktop;

namespace NexusPipeline.Host.Control.Http;

internal sealed class NativeFrontendControl(ControlAuthentication authority, FrontendClients clients,
    DesktopControl desktop, NativeClientSessions sessions)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/desktop/frontend/bind", async (HttpContext context) =>
        {
            var caller = authority.Require(context, ControlCapability.Desktop);
            using var body = await ControlRequestBody.ReadAsync(context);
            var value = body.Root;
            ControlRequestBody.RequireMembers(value, "format", "version", "instanceId", "nativeClientId", "sessionId", "correlationId", "frontendClientId");
            if (value.EnumerateObject().Count() != 7 || value.GetProperty("format").ValueKind != System.Text.Json.JsonValueKind.String ||
                value.GetProperty("format").GetString() != "nexus.desktop.frontend" || value.GetProperty("version").ValueKind != System.Text.Json.JsonValueKind.Number || !value.GetProperty("version").TryGetInt32(out var version) || version != 1 ||
                context.Request.QueryString.HasValue) throw new ControlRequestException(400, "native_frontend_shape_invalid");
            var identity = desktop.RequireNative(context, ControlRequestBody.Uuid(value.GetProperty("instanceId")),
                ControlRequestBody.Uuid(value.GetProperty("nativeClientId")), ControlRequestBody.Uuid(value.GetProperty("sessionId")));
            var correlationId = ControlRequestBody.Uuid(value.GetProperty("correlationId"));
            var client = clients.Read(context, caller) ?? throw new UnauthorizedAccessException("frontend_client_required");
            if (client.ClientId != ControlRequestBody.Uuid(value.GetProperty("frontendClientId")))
                throw new UnauthorizedAccessException("native_frontend_client_mismatch");
            sessions.Bind(client.ClientId, identity, client.RequireCurrent, client.Lifetime, () => desktop.RequireCurrent(identity), desktop.NativeLifetime, desktop.NativeExit);
            return desktop.SignedFrontend(new { format = "nexus.desktop.frontend", version = 1, instanceId = identity.InstanceId,
                nativeClientId = identity.ClientId, sessionId = identity.SessionId, correlationId, frontendClientId = client.ClientId });
        });
        app.MapPost("/control/v1/desktop/frontend/retire", async (HttpContext context) =>
        {
            _ = authority.Require(context, ControlCapability.Desktop);
            using var body = await ControlRequestBody.ReadAsync(context); var value = body.Root;
            ControlRequestBody.RequireMembers(value, "format", "version", "instanceId", "nativeClientId", "sessionId", "correlationId", "frontendClientId");
            if (value.EnumerateObject().Count() != 7 || value.GetProperty("format").ValueKind != System.Text.Json.JsonValueKind.String ||
                value.GetProperty("format").GetString() != "nexus.desktop.frontend" || value.GetProperty("version").ValueKind != System.Text.Json.JsonValueKind.Number || !value.GetProperty("version").TryGetInt32(out var version) || version != 1 ||
                context.Request.QueryString.HasValue) throw new ControlRequestException(400, "native_frontend_shape_invalid");
            var identity = desktop.RequireNative(context, ControlRequestBody.Uuid(value.GetProperty("instanceId")),
                ControlRequestBody.Uuid(value.GetProperty("nativeClientId")), ControlRequestBody.Uuid(value.GetProperty("sessionId")));
            var correlationId = ControlRequestBody.Uuid(value.GetProperty("correlationId"));
            var client = ControlRequestBody.Uuid(value.GetProperty("frontendClientId")); sessions.EndClient(client, identity);
            return desktop.SignedFrontend(new { format = "nexus.desktop.frontend", version = 1, instanceId = identity.InstanceId,
                nativeClientId = identity.ClientId, sessionId = identity.SessionId, correlationId, frontendClientId = client });
        });
    }
}
