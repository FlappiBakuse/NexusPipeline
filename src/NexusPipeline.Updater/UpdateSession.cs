using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Maintenance;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Updater;

internal sealed record UpdateWindowState(string Current, string? Latest, string State, bool CanDownload, int? Progress, string Message);
internal interface IUpdateSession : IDisposable
{
    Task<UpdateWindowState> ReadStatusAsync(CancellationToken token);
    Task CommandAsync(string action, CancellationToken token);
    Task CloseAsync();
}
internal static class UpdateSessions
{
    internal static IUpdateSession Open(string root)
    {
        var payload = ApplicationPayload.ValidateForUpdate(root);
        var owner = InstanceMutex.Acquire();
        if (owner is null)
        {
            int port = InstallerServiceHandoff.FindOwningServicePort(root, payload.ProductVersion) ?? throw new IOException("update.owning_service_unavailable");
            return new RemoteUpdateSession(root, payload.ProductVersion, port);
        }
        try
        {
            if (ConfigUpdateAdmission.HasPendingRecovery(Path.Combine(root, "data"))) throw new IOException("update.configuration_recovery_pending");
            string desktop = Path.Combine(root, ApplicationPayload.DesktopEntry.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(desktop) && RuntimeImageActivityProbe.ObserveExecutable(desktop) != "inactive") throw new IOException("update.desktop_stop_unconfirmed");
            return new LocalUpdateSession(root, payload.ProductVersion, owner);
        }
        catch { owner.ReleaseMutex(); owner.Dispose(); throw; }
    }
    internal static string Message(params string?[] messages) => messages.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? "";
    internal static UpdateWindowState Project(UpdateStatusSnapshot state) => new(state.Current, state.Latest,
        state.State.ToString().ToLowerInvariant(), state.CanDownload, state.Progress,
        Message(state.ErrorCode, state.PolicyError, state.PendingError, state.InstallerToolsWarning, state.Notes));
}
internal sealed class LocalUpdateSession : IUpdateSession
{
    private readonly UpdateEngine _engine;
    private Mutex? _owner;
    internal LocalUpdateSession(string root, string version, Mutex owner)
    {
        _owner = owner;
        var settings = MaintenanceSettings.Read(root);
        var outbound = settings.CreateOutbound();
        _engine = new UpdateEngine(() => settings.Update, version, root, createClient: (uri, timeout, redirects) => outbound.CreateClient(uri, timeout, redirects));
    }
    public Task<UpdateWindowState> ReadStatusAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = _engine.GetStatus();
        return Task.FromResult(UpdateSessions.Project(state));
    }
    public async Task CommandAsync(string action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (action)
        {
            case "check": await _engine.CheckAsync("system", token); break;
            case "download": var download = _engine.StartDownload("system"); if (!download.Succeeded) throw new IOException(download.Code + ": " + download.Error); break;
            case "cancel": _engine.CancelDownload(); break;
            case "apply": var apply = await Task.Run(() => _engine.RequestApply(false, "system"), token); if (!apply.Succeeded) throw new IOException(apply.Code + ": " + apply.Error); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
    public Task CloseAsync() => _engine.CancelAndWaitAsync();
    private void Release() { if (_owner is { } owner) { _owner = null; owner.ReleaseMutex(); owner.Dispose(); } }
    public void Dispose() => Release();
}
internal sealed class RemoteUpdateSession : IUpdateSession
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _root, _version;
    private readonly int _port;
    private readonly ProcessIdentity _identity;
    internal RemoteUpdateSession(string root, string version, int port)
    {
        _root = root; _version = version; _port = port;
        using var process = Process.GetProcessById(int.Parse(File.ReadAllText(new RuntimeStateLayout(root).ServicePidPath)));
        _identity = ProcessIdentity.Capture(process) ?? throw new IOException("update.owning_service_unavailable");
        RequireProcess();
    }
    private void RequireProcess()
    {
        var layout = new RuntimeStateLayout(_root);
        PayloadPathSafety.RequireLinkFree(layout.ServicePidPath); PayloadPathSafety.RequireLinkFree(layout.WebPortPath);
        if (File.ReadAllText(layout.ServicePidPath).Trim() != _identity.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || File.ReadAllText(layout.WebPortPath).Trim() != _port.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new IOException("update.owning_service_changed");
        using var process = Process.GetProcessById(_identity.Pid);
        var current = ProcessIdentity.Capture(process);
        if (current is null || !_identity.Matches(current.Value) || !string.Equals(current.Value.ImageName, Path.Combine(_root, "NexusPipeline.exe"), StringComparison.OrdinalIgnoreCase)) throw new IOException("update.owning_service_changed");
    }
    private async Task<JsonObject> Send(string action, CancellationToken token, JsonObject? body = null)
    {
        try { return await SendCore(action, token, body).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("update.service_timeout"); }
        catch (HttpRequestException) { throw new IOException("update.service_unavailable"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new IOException("update.service_response_invalid"); }
    }
    private async Task<JsonObject> SendCore(string action, CancellationToken token, JsonObject? body)
    {
        RequireProcess();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(action == "check" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(10));
        using (var identityResponse = await _http.GetAsync($"http://127.0.0.1:{_port}/api/status?view=identity", deadline.Token))
        {
            identityResponse.EnsureSuccessStatusCode();
            using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsByteArrayAsync(deadline.Token));
            var value = identity.RootElement;
            if (!value.GetProperty("ready").GetBoolean() || value.GetProperty("version").GetString() != _version
                || value.GetProperty("installationGeneration").GetString() != InstallationGeneration.Id || value.GetProperty("actualPort").GetInt32() != _port) throw new IOException("update.owning_service_changed");
        }
        using var request = new HttpRequestMessage(action == "status" ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{_port}/api/update/{action}");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        var state = JsonNode.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token))?.AsObject() ?? throw new IOException("update.service_response_invalid");
        RequireProcess();
        if (!response.IsSuccessStatusCode) throw new IOException(UpdateSessions.Message(state["code"]?.ToString(), "update.request_rejected"));
        return state;
    }
    internal Task<JsonObject> RequestInstallerApplyAsync(JsonObject body, CancellationToken token) => Send("installer-apply", token, body);
    public async Task<UpdateWindowState> ReadStatusAsync(CancellationToken token)
    {
        var state = await Send("status", token);
        return new(state["current"]!.GetValue<string>(), state["latest"]?.GetValue<string>(), state["state"]!.GetValue<string>(),
            state["canDownload"]?.GetValue<bool>() == true, state["progress"]?.GetValue<int>(),
            UpdateSessions.Message(state["errorCode"]?.ToString(), state["policyError"]?.ToString(), state["pendingError"]?.ToString(), state["installerToolsWarning"]?.ToString(), state["notes"]?.ToString()));
    }
    public async Task CommandAsync(string action, CancellationToken token) => _ = await Send(action, token);
    public Task CloseAsync() => Task.CompletedTask;
    public void Dispose() => _http.Dispose();
}
