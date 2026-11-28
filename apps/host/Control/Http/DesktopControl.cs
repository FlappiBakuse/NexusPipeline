using DesktopSession = NexusPipeline.Core.Desktop.DesktopSession;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using System.Net;
using System.Text.Json;

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

internal sealed class DesktopControl : IAsyncDisposable
{
    private readonly NativeClientBinding _binding;
    private readonly DesktopSession _session;
    private readonly ContractReader _contracts;
    private readonly DesktopResponseSigner _signer;
    private readonly bool _ownsSigner;
    private readonly object _maintenanceGate = new();
    private PreparedDesktopExit? _maintenanceExit;
    private bool _maintenancePermitSent;
    private TaskCompletionSource _maintenanceChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HostLifecycle _lifecycle;
    private readonly DesktopExitWatch _exit;
    private readonly DesktopPresence? _presence;
    private readonly NativeClientSessions? _nativeClients;
    private readonly CancellationTokenSource _nativeEnding;
    private readonly Task _nativeWatch;
    private readonly TaskCompletionSource _nativeExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<NexusPipeline.Core.Settings.DesktopMode>? _publishMode;
    public string VerificationKeyBase64 => _signer.VerificationKeyBase64;

    public DesktopControl(NativeClientBinding binding, Guid instanceId, ContractReader contracts, HostLifecycle lifecycle,
        DesktopExitWatch exit, DesktopResponseSigner? signer = null, DesktopPresence? presence = null, NativeClientSessions? nativeClients = null,
        Action<NexusPipeline.Core.Settings.DesktopMode>? publishMode = null, CancellationToken applicationStopping = default)
    {
        _binding = binding;
        _session = new(instanceId, Guid.NewGuid());
        _contracts = contracts;
        _lifecycle = lifecycle;
        _exit = exit;
        _presence = presence;
        _nativeClients = nativeClients;
        _publishMode = publishMode;
        _nativeEnding = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        _signer = signer ?? new(); _ownsSigner = signer is null;
        _nativeWatch = nativeClients is null ? Task.CompletedTask : WatchNativeAsync();
    }

    internal DesktopSessionIdentity RequireNative(HttpContext context, Guid instanceId, Guid nativeClientId, Guid sessionId)
    {
        if (!IsNative(context)) throw new UnauthorizedAccessException("native_client_required");
        var identity = new DesktopSessionIdentity(instanceId, nativeClientId, sessionId); RequireCurrent(identity); return identity;
    }
    internal void RequireCurrent(DesktopSessionIdentity identity)
    {
        _nativeEnding.Token.ThrowIfCancellationRequested(); _session.RequireCurrent(identity);
        _binding.UseVerifiedLifetime(() => { _session.RequireCurrent(identity); return true; });
    }
    internal CancellationToken NativeLifetime => _nativeEnding.Token;
    internal Task NativeExit => _nativeExited.Task;
    internal IResult SignedFrontend(object payload) => Signed(payload);

    private async Task WatchNativeAsync()
    {
        Exception? failure = null;
        try
        {
            await _binding.WaitForExitAsync(_nativeEnding.Token).ConfigureAwait(false);
            _nativeExited.TrySetResult();
        }
        catch (OperationCanceledException) when (_nativeEnding.IsCancellationRequested) { }
        catch (Exception error) when (error is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { failure = error; }
        finally { EndNativeLifetime(failure); }
    }

    private void EndNativeLifetime(Exception? failure = null)
    {
        var identity = _session.Identity;
        var cleanup = new CleanupFailures();
        cleanup.Attempt(() => _nativeClients?.EndSession(identity.SessionId));
        cleanup.Attempt(_nativeEnding.Cancel);
        if (failure is not null)
        {
            cleanup.Preserve(failure);
            ControlErrors.RecordLifetime("desktop-native-watch", identity.SessionId, failure, identity.InstanceId);
            return;
        }
        try { cleanup.Preserve(); }
        catch (AggregateException error) { ControlErrors.RecordLifetime("desktop-native-cancel", identity.SessionId, error, identity.InstanceId); }
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/control/v1/desktop/session", (HttpContext context) =>
        {
            if (!IsNative(context)) return Results.StatusCode(403);
            if (context.Request.Query.Count != 1 || !Guid.TryParseExact(context.Request.Query["correlationId"], "D", out var correlationId) ||
                correlationId == Guid.Empty) return Results.StatusCode(400);
            var identity = _session.Identity;
            _presence?.Begin(identity.SessionId);
            return Signed(new
            {
                format = "nexus.desktop.session", version = 1, instanceId = identity.InstanceId,
                clientId = identity.ClientId, sessionId = identity.SessionId, correlationId,
                capabilities = new[] { "file.select", "folder.select", "external.open", "close.request" }
            });
        });
        app.MapPost("/control/v1/desktop/authorize", (Delegate)AuthorizeAsync);
        app.MapPost("/control/v1/desktop/mode", (Delegate)PublishModeAsync);
        app.MapGet("/control/v1/desktop/maintenance", async (HttpContext context) =>
        {
            if (!IsNative(context)) return Results.StatusCode(403);
            if (context.Request.Query.Count != 2 ||
                !Guid.TryParseExact(context.Request.Query["sessionId"], "D", out var sessionId) || sessionId != _session.Identity.SessionId ||
                !Guid.TryParseExact(context.Request.Query["correlationId"], "D", out var correlationId) || correlationId == Guid.Empty)
                return Results.StatusCode(400);
            Task? changed;
            lock (_maintenanceGate) changed = _maintenanceExit is null || _maintenancePermitSent ? _maintenanceChanged.Task : null;
            if (changed is not null)
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _nativeEnding.Token);
                try { await changed.WaitAsync(TimeSpan.FromSeconds(25), waiting.Token); }
                catch (TimeoutException) { return Results.NoContent(); }
                catch (OperationCanceledException) when (waiting.IsCancellationRequested) { return Results.NoContent(); }
            }
            if (!IsNative(context) || sessionId != _session.Identity.SessionId) return Results.StatusCode(403);
            lock (_maintenanceGate)
            {
                if (_maintenanceExit is null || _maintenancePermitSent) return Results.NoContent();
                var identity = _session.Identity;
                if (!_session.Authorize(new(identity, correlationId, DesktopAction.RequestClose, null)).IsApproved)
                    return Results.StatusCode(409);
                var decision = _maintenanceExit.RequestPermit(_lifecycle, sessionId, correlationId);
                if (decision.Permit is not { } permit) return Results.StatusCode(409);
                _maintenancePermitSent = true;
                return Signed(new { format = "nexus.desktop.channel", version = 1, instanceId = identity.InstanceId,
                    clientId = identity.ClientId, sessionId, correlationId, type = "close.permit", isApproved = true,
                    permitId = permit.PermitId, expiresAt = permit.ExpiresAt });
            }
        });
    }

    private async Task<IResult> PublishModeAsync(HttpContext context)
    {
        if (!IsNative(context)) return Results.StatusCode(403);
        if (_publishMode is null || _lifecycle.Phase != HostLifecyclePhase.Running) return Results.StatusCode(409);
        if (context.Request.QueryString.HasValue) return Results.StatusCode(400);
        using var body = await ControlRequestBody.ReadAsync(context, 4096);
        var root = body.Root;
        var fields = new[] { "format", "version", "instanceId", "clientId", "sessionId", "correlationId", "mode" };
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length || fields.Any(name => !root.TryGetProperty(name, out _)) ||
            fields.Where(name => name != "version").Any(name => root.GetProperty(name).ValueKind != JsonValueKind.String) ||
            root.GetProperty("format").GetString() != "nexus.desktop.mode" ||
            root.GetProperty("version").ValueKind != JsonValueKind.Number || !root.GetProperty("version").TryGetInt32(out var version) || version != 1)
            return Results.StatusCode(400);
        var identity = new DesktopSessionIdentity(ControlRequestBody.Uuid(root.GetProperty("instanceId")),
            ControlRequestBody.Uuid(root.GetProperty("clientId")), ControlRequestBody.Uuid(root.GetProperty("sessionId")));
        RequireCurrent(identity);
        var correlationId = ControlRequestBody.Uuid(root.GetProperty("correlationId"));
        var mode = root.GetProperty("mode").GetString();
        if (mode is not ("desktop" or "browser")) return Results.StatusCode(400);
        _binding.UseVerifiedLifetime(() =>
        {
            RequireCurrent(identity);
            if (_lifecycle.Phase != HostLifecyclePhase.Running) throw new IOException("desktop_mode_host_unavailable");
            _publishMode(mode == "desktop" ? NexusPipeline.Core.Settings.DesktopMode.Desktop : NexusPipeline.Core.Settings.DesktopMode.Browser);
            return true;
        });
        return Signed(new { format = "nexus.desktop.mode", version = 1, instanceId = identity.InstanceId,
            clientId = identity.ClientId, sessionId = identity.SessionId, correlationId, mode });
    }

    internal void PrepareMaintenanceExit(PreparedDesktopExit prepared)
    {
        lock (_maintenanceGate)
        {
            if (_maintenanceExit is not null) throw new IOException("desktop_maintenance_exit_already_pending");
            _maintenanceExit = prepared; _maintenancePermitSent = false;
            var changed = _maintenanceChanged;
            _maintenanceChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    internal void ClearMaintenanceExit(PreparedDesktopExit prepared)
    {
        lock (_maintenanceGate)
            if (ReferenceEquals(_maintenanceExit, prepared)) { _maintenanceExit = null; _maintenancePermitSent = false; }
    }

    private async Task<IResult> AuthorizeAsync(HttpContext context)
    {
        if (!IsNative(context)) return Results.StatusCode(403);
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
        var buffer = new byte[8193];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted);
            if (read == 0) break;
            length += read;
        }
        if (length == buffer.Length) return Results.StatusCode(413);
        try
        {
            using var document = _contracts.Read(buffer.AsMemory(0, length), "desktop-channel.schema.json");
            var message = document.RootElement;
            var action = message.GetProperty("type").GetString() switch
            {
                "file.select" => DesktopAction.SelectFile,
                "folder.select" => DesktopAction.SelectFolder,
                "external.open" => DesktopAction.OpenExternal,
                "close.request" => DesktopAction.RequestClose,
                _ => (DesktopAction?)null
            };
            if (action is null) return Results.StatusCode(403);
            if (action != DesktopAction.RequestClose && _lifecycle.Phase != HostLifecyclePhase.Running) return Results.StatusCode(409);
            var identity = new DesktopSessionIdentity(message.GetProperty("instanceId").GetGuid(),
                message.GetProperty("clientId").GetGuid(), message.GetProperty("sessionId").GetGuid());
            var correlationId = message.GetProperty("correlationId").GetGuid();
            var url = message.TryGetProperty("url", out var value) ? value.GetString() : null;
            var authorization = _session.Authorize(new(identity, correlationId, action.Value, url));
            if (!authorization.IsApproved) return Results.StatusCode(409);
            if (action == DesktopAction.RequestClose) return await PermitCloseAsync(context, identity, correlationId);
            return Signed(new
            {
                format = "nexus.desktop.channel", version = 1, instanceId = identity.InstanceId,
                clientId = identity.ClientId, sessionId = identity.SessionId, correlationId,
                type = "reply", isApproved = true
            });
        }
        catch (JsonException) { return Results.StatusCode(400); }
    }

    private async Task<IResult> PermitCloseAsync(HttpContext context, DesktopSessionIdentity identity, Guid correlationId)
    {
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _nativeEnding.Token);
        drain.CancelAfter(TimeSpan.FromSeconds(25));
        var granted = false;
        try
        {
            drain.Token.ThrowIfCancellationRequested();
            var decision = _lifecycle.RequestClose(identity.SessionId, correlationId);
            while (decision.ReasonCode == "accepted_writes_pending")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), drain.Token);
                decision = _lifecycle.ContinueClose(identity.SessionId, correlationId);
            }
            if (decision.Permit is not { } permit) return CloseRejected(identity, correlationId, decision.ReasonCode);
            var reply = Signed(new
            {
                format = "nexus.desktop.channel", version = 1, instanceId = identity.InstanceId, clientId = identity.ClientId,
                sessionId = identity.SessionId, correlationId, type = "close.permit", isApproved = true,
                permitId = permit.PermitId, expiresAt = permit.ExpiresAt
            });
            drain.Token.ThrowIfCancellationRequested();
            _exit.Begin(permit);
            granted = true;
            return reply;
        }
        catch (OperationCanceledException) when (drain.IsCancellationRequested)
        {
            if (context.RequestAborted.IsCancellationRequested || _nativeEnding.IsCancellationRequested) return Results.NoContent();
            return CloseRejected(identity, correlationId, "desktop_close_pending");
        }
        finally
        {
            if (!granted) _lifecycle.AbandonClose(identity.SessionId, correlationId);
        }
    }

    private IResult CloseRejected(DesktopSessionIdentity identity, Guid correlationId, string code) => Signed(new
    {
        format = "nexus.desktop.channel", version = 1, instanceId = identity.InstanceId, clientId = identity.ClientId,
        sessionId = identity.SessionId, correlationId, type = "close.rejected", isApproved = false, code
    }, StatusCodes.Status409Conflict);

    private bool IsNative(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var connection = context.Connection;
        return connection.RemoteIpAddress is { } remote && connection.LocalIpAddress is { } local &&
            _binding.Matches(new IPEndPoint(remote, connection.RemotePort), new IPEndPoint(local, connection.LocalPort));
    }

    private IResult Signed(object payload, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(_signer.Sign(JsonSerializer.SerializeToUtf8Bytes(payload)), statusCode: statusCode);
    public async ValueTask DisposeAsync()
    {
        try
        {
            EndNativeLifetime();
            await _nativeWatch.ConfigureAwait(false); await _exit.DisposeAsync();
        }
        finally
        {
            _presence?.End(_session.Identity.SessionId);
            _session.End();
            _nativeEnding.Dispose();
            if (_ownsSigner) _signer.Dispose();
        }
    }
}
