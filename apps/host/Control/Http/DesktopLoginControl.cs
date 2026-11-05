using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

internal sealed class DesktopLoginControl(ControlAuthentication authority, DesktopControl desktop,
    BrowserLoginSessions sessions, ContractReader contracts)
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/desktop/login", async (HttpContext context) =>
        {
            _ = authority.Require(context, ControlCapability.Desktop);
            if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid");
            using var body = await ControlRequestBody.ReadAsync(context, 262144);
            body.RequireSchema(contracts, "desktop-login.schema.json"); var value = body.Root;
            var native = desktop.RequireNative(context, ControlRequestBody.Uuid(value.GetProperty("instanceId")),
                ControlRequestBody.Uuid(value.GetProperty("nativeClientId")), ControlRequestBody.Uuid(value.GetProperty("sessionId")));
            var correlation = ControlRequestBody.Uuid(value.GetProperty("correlationId"));
            if (value.GetProperty("type").GetString() == "poll")
            {
                var offers = sessions.Poll(native).Select(offer => new { captureId = offer.CaptureId, nonce = offer.Nonce,
                    clientId = offer.ClientId, extensionId = offer.ExtensionId, activationId = offer.ActivationId, operationId = offer.OperationId,
                    policy = JsonSerializer.SerializeToElement(offer.Policy, Wire), isCanceled = offer.IsCanceled }).ToArray();
                return desktop.SignedFrontend(new { format = "nexus.desktop.login", version = 1, instanceId = native.InstanceId,
                    nativeClientId = native.ClientId, sessionId = native.SessionId, correlationId = correlation, type = "offers", offers });
            }
            if (value.GetProperty("type").GetString() != "complete") throw new ControlRequestException(400, "browser_login_request_invalid");
            var capture = ControlRequestBody.Uuid(value.GetProperty("captureId")); var nonce = ControlRequestBody.Uuid(value.GetProperty("nonce"));
            var values = new Dictionary<string, SecretValue>(StringComparer.Ordinal);
            try
            {
                foreach (var member in value.GetProperty("values").EnumerateObject()) values.Add(member.Name, new(member.Value.GetString()!));
                sessions.Complete(native, capture, nonce, value.GetProperty("status").GetString()!, values);
                return desktop.SignedFrontend(new { format = "nexus.desktop.login", version = 1, instanceId = native.InstanceId,
                    nativeClientId = native.ClientId, sessionId = native.SessionId, correlationId = correlation, type = "completed", captureId = capture, nonce });
            }
            finally { foreach (var secret in values.Values) secret.Dispose(); }
        });
    }
}
