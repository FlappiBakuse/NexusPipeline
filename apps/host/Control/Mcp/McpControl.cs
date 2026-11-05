using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Operations;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Mcp;

[SupportedOSPlatform("windows")]
internal sealed class McpControl(ControlAuthentication authority, McpControlTools tools)
{
    private const string Version = "2025-11-25";
    private readonly McpSessions _sessions = new();
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/mcp", (Func<HttpContext, Task<IResult>>)PostAsync);
        app.MapGet("/control/v1/mcp", (HttpContext context) =>
        {
            _ = Require(context);
            context.Response.Headers.Allow = "POST, DELETE";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });
        app.MapDelete("/control/v1/mcp", (HttpContext context) =>
        {
            var request = Require(context);
            RequireVersion(context);
            try { _sessions.End(Session(context), request.SubjectId); return Results.NoContent(); }
            catch (McpProtocolException) { return Results.NotFound(); }
        });
    }

    private async Task<IResult> PostAsync(HttpContext context)
    {
        var request = Require(context);
        if (!Accepts(context, "application/json") || !Accepts(context, "text/event-stream"))
            throw new ControlRequestException(406, "mcp_accept_invalid");
        ControlJsonBody body;
        try { body = await ControlRequestBody.ReadAsync(context); }
        catch (JsonException)
        { return Results.Json(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "parse_error" } }); }
        using var owned = body;
        JsonElement? id = null;
        try
        {
            var root = body.Root;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(member => member.Name is not ("jsonrpc" or "id" or "method" or "params")))
                throw new McpProtocolException(-32600, "request_invalid");
            if (!root.TryGetProperty("jsonrpc", out var rpc) || rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0" ||
                !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
                method.GetString() is not { Length: > 0 and <= 128 })
                throw new McpProtocolException(-32600, "request_invalid");
            var methodName = method.GetString()!;
            if (root.TryGetProperty("id", out var identity)) id = ReadId(identity);
            var parameters = root.TryGetProperty("params", out var value) ? value : default;
            if (methodName == "initialize")
            {
                if (id is null || context.Request.Headers.ContainsKey("Mcp-Session-Id"))
                    throw new McpProtocolException(-32600, "initialization_invalid");
                return Initialize(context, request, parameters, id.Value);
            }
            RequireVersion(context);
            var session = Session(context);
            if (methodName == "notifications/initialized")
            {
                if (id is not null || parameters.ValueKind != JsonValueKind.Undefined &&
                    (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Any()))
                    throw new McpProtocolException(-32600, "initialization_invalid");
                _sessions.Require(session, request.SubjectId, initialize: true);
                return Results.StatusCode(StatusCodes.Status202Accepted);
            }
            _sessions.Require(session, request.SubjectId);
            if (id is null) return Results.StatusCode(StatusCodes.Status202Accepted);
            var result = methodName switch
            {
                "ping" => Empty(parameters),
                "tools/list" => List(request, parameters),
                "tools/call" => await CallAsync(request, parameters),
                _ => throw new McpProtocolException(-32601, "method_unknown")
            };
            return Reply(id.Value, result);
        }
        catch (McpProtocolException error)
        { return Results.Json(new { jsonrpc = "2.0", id, error = new { code = error.Code, message = error.Message } }, statusCode: error.Code == -32001 ? 404 : 200); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or ControlRequestException)
        { return Results.Json(new { jsonrpc = "2.0", id, error = new { code = -32602, message = "request_invalid" } }); }
    }

    private IResult Initialize(HttpContext context, AuthenticatedControlRequest request, JsonElement value, JsonElement id)
    {
        ControlRequestBody.RequireMembers(value, "protocolVersion", "capabilities", "clientInfo");
        if (!value.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() is not { Length: > 0 and <= 32 } ||
            !value.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("clientInfo", out var client) || client.ValueKind != JsonValueKind.Object)
            throw new McpProtocolException(-32602, "initialization_invalid");
        ControlRequestBody.RequireMembers(client, "name", "title", "version", "websiteUrl", "icons");
        if (!client.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            name.GetString() is not { Length: > 0 and <= 128 } ||
            !client.TryGetProperty("version", out var clientVersion) || clientVersion.ValueKind != JsonValueKind.String ||
            clientVersion.GetString() is not { Length: > 0 and <= 64 })
            throw new McpProtocolException(-32602, "initialization_invalid");
        context.Response.Headers["Mcp-Session-Id"] = _sessions.Initialize(request.SubjectId);
        return Reply(id, new { protocolVersion = Version, capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "NexusPipeline", version = "0.18.0" } });
    }

    private async Task<object> CallAsync(AuthenticatedControlRequest request, JsonElement parameters)
    {
        try { return await tools.CallAsync(request, parameters); }
        catch (UnauthorizedAccessException) { return McpNotificationTools.Failure("access_denied"); }
        catch (OperationConflictException) { return McpNotificationTools.Failure("operation_conflict"); }
        catch (NotificationTaskException error) { return McpNotificationTools.Failure(error.Message); }
        catch (RetiredContentException) { return McpNotificationTools.Failure("record_content_retired"); }
        catch (IOException) { return McpNotificationTools.Failure("resource_unavailable"); }
        catch (InvalidOperationException) { return McpNotificationTools.Failure("operation_unavailable"); }
    }

    private object List(AuthenticatedControlRequest request, JsonElement parameters)
    { _ = Empty(parameters); return tools.List(request); }
    private static object Empty(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Undefined) ControlRequestBody.RequireMembers(parameters);
        return new { };
    }
    private static IResult Reply(JsonElement id, object result) =>
        Results.Json(new { jsonrpc = "2.0", id, result }, NotificationUseCases.Json);
    private AuthenticatedControlRequest Require(HttpContext context)
    {
        if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid");
        var request = authority.Require(context, ControlCapability.CapabilitiesRead);
        if (request.Origin != ControlOrigin.Mcp) throw new UnauthorizedAccessException();
        return request;
    }
    private static void RequireVersion(HttpContext context)
    {
        if (context.Request.Headers["MCP-Protocol-Version"] is not { Count: 1 } version || version[0] != Version)
            throw new ControlRequestException(400, "mcp_version_invalid");
    }
    private static string Session(HttpContext context) =>
        context.Request.Headers["Mcp-Session-Id"] is { Count: 1 } values && values[0] is { Length: 64 } id &&
        id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ? id :
        throw new McpProtocolException(-32001, "session_invalid");
    private static JsonElement ReadId(JsonElement id) =>
        id.ValueKind == JsonValueKind.String && id.GetString() is { Length: <= 128 } ||
        id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) && value is >= -9007199254740991 and <= 9007199254740991
            ? id.Clone() : throw new McpProtocolException(-32600, "request_identity_invalid");
    private static bool Accepts(HttpContext context, string mediaType)
    {
        try
        {
            return context.Request.GetTypedHeaders().Accept?.Any(value =>
                value.MediaType.Value!.Equals(mediaType, StringComparison.OrdinalIgnoreCase) &&
                value.Quality.GetValueOrDefault(1) > 0) == true;
        }
        catch (FormatException) { return false; }
    }
}
