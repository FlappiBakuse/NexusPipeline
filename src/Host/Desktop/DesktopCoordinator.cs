using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Host.Desktop;

internal sealed class DesktopCoordinator(Func<bool> requestExit) : IDesktopHost, IAsyncDisposable
{
    private readonly object _state = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _create = new(1, 1);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly string _rootHash = DesktopSupervisorProtocol.RootHash(AppPaths.AppRoot);
    private readonly DesktopSessionStore _store = new(AppPaths.DesktopRuntimeDir, DesktopSupervisorProtocol.RootHash(AppPaths.AppRoot));
    private DesktopSessionRecord? _session;
    private byte[]? _key;
    private NamedPipeServerStream? _desktopPipe;
    private Task? _listener;
    private Task? _monitor;
    private bool _unowned, _pendingShow, _handoff, _stopping;
    private int _port, _ready;
    private string _previousInstance = "";
    private Process? _launchedProcess;
    private int _disposed;
    internal bool RestoredVisible => _session?.WindowState == "visible";

    public void Start()
    {
        if (_listener is not null) return;
        var saved = _store.Load();
        if (saved is { } owned)
        {
            if (owned.Record.Main.IsAlive() && string.Equals(owned.Record.Main.ExecutablePath, AppPaths.DesktopExecutablePath, StringComparison.OrdinalIgnoreCase))
            { _session = owned.Record; _key = owned.Key; _previousInstance = owned.Record.HostInstanceId; }
            else { _store.RemoveOwned(owned.Record, owned.Key); CryptographicOperations.ZeroMemory(owned.Key); }
        }
        _unowned = _session is null && _store.HasFiles;
        if (_unowned) Logger.Warn("desktop_session_unowned");
        _listener = ListenAsync();
        _monitor = MonitorAsync();
    }
    public void MarkReady(int actualPort)
    {
        _port = actualPort;Volatile.Write(ref _ready, 1);
        if (_desktopPipe is not null) _ = SendStateAsync();
        if (_pendingShow) _ = ShowAsync("user");
    }
    public async Task<bool> ShowAsync(string reason, CancellationToken token = default)
    {
        _pendingShow = true;
        if (Volatile.Read(ref _ready) == 0) return true;
        await _create.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_stopping || _unowned || HostInstance.Identity is null) return false;
            if (_session?.Main.IsAlive() == true)
            {
                if (_desktopPipe is not null) { await SendAsync("window.show", new { reason }, token).ConfigureAwait(false);_pendingShow = false; }
                return true;
            }
            if (_session is not null && _key is not null)
            {
                if (_session.Family.Any(identity => !identity.HasExited())) return false;
                _store.RemoveOwned(_session, _key);
                if (_store.HasFiles) return false;
            }
            if (!File.Exists(AppPaths.DesktopExecutablePath)) { Logger.Warn("desktop_bundle_missing");return false; }
            byte[] key = _store.CreateKey();
            string sessionId = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(AppPaths.DesktopExecutablePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.AppRoot, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { "--supervisor-pipe", DesktopSupervisorProtocol.PipeName(_rootHash), "--desktop-session", sessionId, "--install-root-hash", _rootHash, "--launch-intent", "show" }) start.ArgumentList.Add(value);
            start.Environment["NEXUS_DESKTOP_SESSION_KEY"] = Convert.ToHexString(key).ToLowerInvariant();
            Process process = Process.Start(start) ?? throw new IOException("Desktop launch failed");
            _launchedProcess = process;
            process.OutputDataReceived += (_, _) => { };process.ErrorDataReceived += (_, _) => { };process.BeginOutputReadLine();process.BeginErrorReadLine();
            int restartCount = reason == "main-crash" ? 1 : _session?.MainRestartCount ?? 0;
            lock (_state)
            {
            _session = new(1, _rootHash, "g0170", sessionId, HostInstance.DesktopBuildId, HostInstance.Id, DesktopProcessIdentity.Read(process.Id), "hidden", "#/dashboard", "") { MainRestartCount = restartCount };
            _key = key;_store.Save(_session, key);_pendingShow = false;
            }
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { Logger.Warn("desktop_launch_failed: " + exception.GetType().Name);return false; }
        finally { _create.Release(); }
    }
    public async Task PrepareHostRestartAsync(string handoffId, CancellationToken token = default)
    {
        _handoff = true;
        if (_session is null || _key is null) return;
        lock (_state)
        {
            _session = _session with { HandoffId = handoffId };
            _store.Save(_session, _key);
        }
        if (_desktopPipe is not null) await SendAsync("host.restart-preparing", new { handoffId }, token).ConfigureAwait(false);
    }
    public Task<bool> PrepareAssetReplacementAsync(string transactionId, TimeSpan remaining, CancellationToken token = default)
        => StopDesktopAsync("update", transactionId, remaining, token);
    public async Task StopForHostExitAsync(CancellationToken token = default)
    {
        // Host shutdown waits on the STA after its message loop has stopped.
        if (!_handoff) await StopDesktopAsync("host-exit", "", TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
    }
    private async Task<bool> StopDesktopAsync(string reason, string transactionId, TimeSpan remaining, CancellationToken token)
    {
        _stopping = true;
        if (_session is null || _key is null) return !_unowned;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(remaining);
        try
        {
            if (_session.Main.IsAlive())
            {
                CaptureFamily();
                if (_desktopPipe is null) { _stopping = false; return false; }
                await SendAsync("desktop.prepare-stop", new { reason, transactionId, remainingMs = Math.Max(0, (int)remaining.TotalMilliseconds) }, deadline.Token).ConfigureAwait(false);
            }
            while (!_session.Main.HasExited() || _session.Family.Any(identity => !identity.HasExited()))
            {
                if (_session.Main.IsAlive()) CaptureFamily();
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
            _store.RemoveOwned(_session, _key);
            return !_store.HasFiles;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        { _stopping = false; Logger.Warn("desktop_stop_unconfirmed"); return false; }
    }
    private void CaptureFamily()
    {
        lock (_state)
        {
        if (_session is null || _key is null || !_session.Main.IsAlive()) return;
        var family = _session.Family.Where(identity => !identity.HasExited()).Concat(DesktopProcessFamily.Capture(_session.Main, AppPaths.DesktopBundleDir)).Distinct().ToArray();
        if (family.Length > 128) throw new IOException("desktop_family_limit");
        if (!_session.Family.SequenceEqual(family)) { _session = _session with { Family = family }; _store.Save(_session, _key); }
        }
    }
    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                if (_stopping || _handoff || _session is null) continue;
                if (_session.Main.IsAlive()) { CaptureFamily(); continue; }
                if (_session.MainRestartCount == 0 && _session.WindowState == "visible" && !_session.Family.Any(identity => !identity.HasExited()))
                    await ShowAsync("main-crash", _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { _unowned = true; Logger.Warn("desktop_family_unconfirmed"); }
    }
    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = DesktopPipeTransport.Create(DesktopSupervisorProtocol.PipeName(_rootHash));
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                _ = HandleAsync(pipe);pipe = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Logger.Warn("desktop_pipe_unavailable: " + exception.GetType().Name);await Task.Delay(500, _stop.Token).ConfigureAwait(false); }
            finally { pipe?.Dispose(); }
        }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);handshake.CancelAfter(TimeSpan.FromSeconds(5));
            DesktopProcessIdentity peer = NamedPipePeerIdentity.Client(pipe);
            JsonElement message = await DesktopPipeTransport.ReadAsync(pipe, handshake.Token);
            DesktopSupervisorProtocol.Fields(message, "type", "requestId", "data");
            JsonElement hello = message.GetProperty("data");
            DesktopSupervisorProtocol.Fields(hello, "role", "protocol", "rootHash", "generation", "sessionId", "clientNonce");
            string requestId = message.GetProperty("requestId").GetString()!;
            if (message.GetProperty("type").GetString() != "hello" || requestId.Length > 64 || hello.GetProperty("protocol").GetInt32() != 1 || hello.GetProperty("rootHash").GetString() != _rootHash || hello.GetProperty("generation").GetString() != "g0170") return;
            string role = hello.GetProperty("role").GetString()!;
            DesktopProcessIdentity host = DesktopProcessIdentity.Read(Environment.ProcessId);
            if (role == "activation")
            {
                if (!string.Equals(peer.ExecutablePath, Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe"), StringComparison.OrdinalIgnoreCase) || hello.GetProperty("sessionId").GetString() != "") return;
                await DesktopPipeTransport.WriteAsync(pipe, new { type = "activation-attached", requestId, data = new { hostPid = host.Pid, hostStartFileTime = host.StartFileTime } }, handshake.Token);
                JsonElement request = await DesktopPipeTransport.ReadAsync(pipe, handshake.Token);DesktopSupervisorProtocol.Fields(request, "type", "requestId", "data");DesktopSupervisorProtocol.Fields(request.GetProperty("data"));
                if (request.GetProperty("type").GetString() != "window.show" || request.GetProperty("requestId").GetString() != requestId) return;
                bool shown = await ShowAsync("user", handshake.Token);
                await DesktopPipeTransport.WriteAsync(pipe, new { type = "window.show-result", requestId, data = new { accepted = shown } }, handshake.Token);return;
            }
            if (role != "desktop" || _session is null || _key is null || peer != _session.Main || hello.GetProperty("sessionId").GetString() != _session.SessionId) return;
            string clientNonce = hello.GetProperty("clientNonce").GetString()!, hostNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            if (clientNonce.Length != 64 || !clientNonce.All(char.IsAsciiHexDigitLower)) return;
            await DesktopPipeTransport.WriteAsync(pipe, new { type = "challenge", requestId, data = new { hostNonce, instanceId = HostInstance.Id, buildId = HostInstance.DesktopBuildId, hostPid = host.Pid, hostStartFileTime = host.StartFileTime, proof = DesktopSupervisorProtocol.Proof(_key, "host", _rootHash, _session.SessionId, clientNonce, hostNonce, HostInstance.Id, HostInstance.DesktopBuildId) } }, handshake.Token);
            JsonElement response = await DesktopPipeTransport.ReadAsync(pipe, handshake.Token);DesktopSupervisorProtocol.Fields(response, "type", "requestId", "data");DesktopSupervisorProtocol.Fields(response.GetProperty("data"), "proof");
            if (response.GetProperty("type").GetString() != "proof" || response.GetProperty("requestId").GetString() != requestId || !DesktopSupervisorProtocol.EqualsProof(response.GetProperty("data").GetProperty("proof").GetString()!, DesktopSupervisorProtocol.Proof(_key, "client", _rootHash, _session.SessionId, clientNonce, hostNonce, HostInstance.Id, HostInstance.DesktopBuildId))) return;
            if (Interlocked.CompareExchange(ref _desktopPipe, pipe, null) is not null) return;
            await DesktopPipeTransport.WriteAsync(pipe, new { type = "attached", requestId, data = new { } }, handshake.Token);
            await SendStateAsync();
            if (_pendingShow) await ShowAsync("user", _stop.Token);
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(5));
            Task pulse = PulseAsync(heartbeat);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    JsonElement command = await DesktopPipeTransport.ReadAsync(pipe, deadline.Token);DesktopSupervisorProtocol.Fields(command, "type", "requestId", "data");
                    if (command.GetProperty("requestId").GetString() is not { Length: > 0 and <= 64 }) return;
                    JsonElement data = command.GetProperty("data");
                    switch (command.GetProperty("type").GetString())
                    {
                        case "window.state":
                            DesktopSupervisorProtocol.Fields(data, "state", "route");
                            string state = data.GetProperty("state").GetString()!, route = data.GetProperty("route").GetString()!;
                            if (state is not ("hidden" or "visible" or "minimized") || route.Length > 256 || !System.Text.RegularExpressions.Regex.IsMatch(route, "^#/[a-zA-Z0-9_/-]*$")) return;
                            lock (_state)
                            {
                                _session = _session with { WindowState = state, Route = route, HostInstanceId = HostInstance.Id };_store.Save(_session, _key);
                            }
                            break;
                        case "window.show-request": DesktopSupervisorProtocol.Fields(data);await ShowAsync("user", _stop.Token);break;
                        case "host.exit-request": DesktopSupervisorProtocol.Fields(data);requestExit();break;
                        case "window.show-result": DesktopSupervisorProtocol.Fields(data, "result");break;
                        case "desktop.stop-ready": case "pong": DesktopSupervisorProtocol.Fields(data);break;
                        default: return;
                    }
                }
            }
            finally { heartbeat.Dispose();await pulse.ConfigureAwait(false); }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException) { }
        finally { Interlocked.CompareExchange(ref _desktopPipe, null, pipe);pipe.Dispose(); }
    }
    private async Task PulseAsync(PeriodicTimer timer)
    {
        try { while (await timer.WaitForNextTickAsync(_stop.Token)) await SendAsync("ping", new { }, _stop.Token); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private Task SendStateAsync() => SendAsync("host.state", new { state = Volatile.Read(ref _ready) != 0 ? "ready" : "starting", actualPort = _port, instanceId = HostInstance.Id, previousInstanceId = _previousInstance, handoffId = HostInstance.RestartHandoffId, desktopBuildId = HostInstance.DesktopBuildId, frontendBuildId = HostInstance.FrontendBuildId }, _stop.Token);
    private async Task SendAsync(string type, object data, CancellationToken token)
    {
        NamedPipeServerStream? pipe = Volatile.Read(ref _desktopPipe);if (pipe is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await _write.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { await DesktopPipeTransport.WriteAsync(pipe, new { type, requestId = Guid.NewGuid().ToString("N"), data }, deadline.Token).ConfigureAwait(false); }
        finally { _write.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopForHostExitAsync().ConfigureAwait(false);_stop.Cancel();_desktopPipe?.Dispose();
        if (_listener is not null) { try { await _listener.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        if (_monitor is not null) await _monitor.ConfigureAwait(false);
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);_launchedProcess?.Dispose();_stop.Dispose();_create.Dispose();_write.Dispose();
    }
}
