using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.BrowserLogin;

internal sealed class BrowserLoginService : IDisposable
{
    private sealed record Flow(string Owner, PluginBrowserLoginFlow Spec,
        Func<PluginBrowserCapture, PluginBrowserInvocation, CancellationToken, ValueTask<PluginBrowserLoginResult>> Complete,
        Func<PluginBrowserInvocation, PluginBrowserLoginTerminal, CancellationToken, ValueTask> Terminal,
        Action<PluginBrowserInvocation> ValidateInvocation);
    private sealed class Operation(Flow flow, PluginBrowserInvocation invocation)
    {
        internal readonly Flow Flow = flow;
        internal readonly PluginBrowserInvocation Invocation = invocation;
        internal readonly SemaphoreSlim TerminalGate = new(1, 1);
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(invocation.ClientSession.Lifetime);
        internal DateTimeOffset CreatedAt;
        internal long Created;
        internal CancellationTokenRegistration Cancellation;
        internal int State, CancelRequested;
        internal PluginBrowserLoginResult? Result;
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Flow> _flows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private readonly System.Threading.Timer _sweep;
    private readonly TimeProvider _time;
    private bool _disposed;
    internal event Action<string, string>? CancelRequested;
    internal BrowserLoginService(TimeProvider? time = null)
    { _time = time ?? TimeProvider.System; _sweep = new(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)); }

    internal IDisposable Register(string owner, PluginBrowserLoginFlow flow,
        Func<PluginBrowserCapture, PluginBrowserInvocation, CancellationToken, ValueTask<PluginBrowserLoginResult>> complete,
        Func<PluginBrowserInvocation, PluginBrowserLoginTerminal, CancellationToken, ValueTask> terminal,
        Action<PluginBrowserInvocation> validateInvocation)
    {
        Validate(flow);
        string key = owner + ":" + flow.Id;
        var entry = new Flow(owner, flow with
        {
            NavigationOrigins = flow.NavigationOrigins.ToArray(), PopupOrigins = flow.PopupOrigins.ToArray(),
            Cookies = flow.Cookies.ToArray(), Storage = flow.Storage.Select(r => r with { JsonPath = r.JsonPath.ToArray() }).ToArray(),
        }, complete, terminal, validateInvocation);
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); if (!_flows.TryAdd(key, entry)) throw new InvalidOperationException("browser_flow_duplicate"); }
        return new Registration(() =>
        {
            Operation[] active;
            lock (_sync) { if (!_flows.Remove(key)) return; active = _operations.Values.Where(o => o.Flow == entry && o.State < 4).ToArray(); }
            foreach (var op in active) _ = CancelAsync(op, "service_stopping");
        });
    }

    internal object Begin(PluginClientSessionContext client, string flowId, string editorSessionId, long fieldGeneration, JsonObject context)
    {
        if (!client.NativeBrowserAvailable || client.ClientKind != "desktop" || client.Lifetime.IsCancellationRequested) throw new InvalidOperationException("client_not_supported");
        if (editorSessionId.Length is < 1 or > 64 || fieldGeneration < 0 || Encoding.UTF8.GetByteCount(context.ToJsonString()) > 4096) throw new InvalidDataException("browser_invocation_invalid");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_flows.TryGetValue(flowId, out var flow) || !flow.Spec.Ready) throw new InvalidOperationException("platform_not_ready");
            var active = _operations.Values.FirstOrDefault(o => SameClient(o, client) && o.State < 4);
            if (active is not null)
            {
                if (active.Flow == flow && active.Invocation.EditorSessionId == editorSessionId && active.Invocation.FieldGeneration == fieldGeneration)
                    return Descriptor(active);
                throw new InvalidOperationException("browser_operation_active");
            }
            if (_operations.Count >= 256) throw new InvalidOperationException("browser_operation_limit");
            string id = Guid.NewGuid().ToString("N");
            var invocation = new PluginBrowserInvocation(id, editorSessionId, fieldGeneration, (JsonObject)context.DeepClone(), client);
            flow.ValidateInvocation(invocation);
            var op = new Operation(flow, invocation) { CreatedAt = _time.GetUtcNow(), Created = _time.GetTimestamp() };
            _operations.Add(id, op);
            op.Cancellation = client.Lifetime.Register(() => { _ = CancelAsync(op, "client_session_expired"); });
            return Descriptor(op);
        }
    }

    private static object Descriptor(Operation op) => new
    {
        operationId = op.Invocation.OperationId, flow = op.Flow.Spec,
        expiresAt = op.CreatedAt.AddMinutes(15),
    };
    internal object Status(PluginClientSessionContext client, string id)
    {
        var op = Find(client, id);
        lock (_sync) return new { operationId = id, state = op.State switch { 0 => "open", 1 => "validating", 2 or 3 => "cleaning", 4 => "completed", 5 => "cancelled", _ => "failed" }, result = op.State >= 4 ? op.Result : null };
    }
    internal async Task<PluginBrowserLoginResult> CompleteAsync(PluginClientSessionContext client, string id, PluginBrowserCapture capture)
    {
        var op = Find(client, id);
        if (Interlocked.CompareExchange(ref op.State, 1, 0) != 0) return new(false, Error: "browser_operation_busy");
        try
        {
            ValidateCapture(op.Flow.Spec, capture);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(op.Stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await op.Flow.Complete(capture, op.Invocation, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!result.Success)
            {
                Interlocked.CompareExchange(ref op.State, 0, 1);
                return new(false, Error: SafeError(result.Error));
            }
            if (result.Source != "browser" || !result.Configured || result.CandidateId is not { Length: > 0 and <= 64 }
                || result.MaskedAccount is not { Length: > 0 and <= 120 }) throw new InvalidDataException("browser_result_invalid");
            lock (_sync)
            {
                if (op.CancelRequested != 0 || op.State != 1 || op.Stop.IsCancellationRequested) throw new OperationCanceledException();
                op.Result = result;
                op.State = 2;
            }
            // The desktop receives only an instruction to clean; candidate publication waits for its receipt.
            return new(true);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(op, "browser_validation_cancelled").ConfigureAwait(false);
            return new(false, Error: "browser_validation_cancelled");
        }
        catch
        {
            Interlocked.CompareExchange(ref op.State, 0, 1);
            return new(false, Error: "browser_validation_failed");
        }
    }

    internal async Task CleanedAsync(PluginClientSessionContext client, string id, bool cleaned)
    {
        var op = Find(client, id);
        await op.TerminalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (op.State >= 4) return;
            if (!cleaned || op.State != 2 || op.CancelRequested != 0 || op.Stop.IsCancellationRequested)
            { await FinishFailedAsync(op, "browser_cleanup_failed").ConfigureAwait(false); return; }
            op.State = 3;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await op.Flow.Terminal(op.Invocation, PluginBrowserLoginTerminal.Completed, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    if (op.CancelRequested == 0 && !op.Stop.IsCancellationRequested) { op.State = 4; op.Cancellation.Unregister(); return; }
                }
                await FinishFailedAsync(op, "browser_operation_cancelled").ConfigureAwait(false);
            }
            catch { await FinishFailedAsync(op, "browser_activation_failed").ConfigureAwait(false); }
        }
        finally { op.TerminalGate.Release(); }
    }

    internal Task CancelAsync(PluginClientSessionContext client, string id) => CancelAsync(Find(client, id), "browser_operation_cancelled");
    internal Task CancelEditorAsync(string owner, PluginClientSessionContext client, string editor)
    {
        Operation[] active;
        lock (_sync) active = _operations.Values.Where(o => o.Flow.Owner == owner && SameClient(o, client) && o.Invocation.EditorSessionId == editor && o.State < 4).ToArray();
        return Task.WhenAll(active.Select(o => CancelAsync(o, "credential_editor_closed")));
    }
    private async Task CancelAsync(Operation op, string error)
    {
        lock (_sync) { if (op.State >= 4) return; op.CancelRequested = 1; }
        op.Stop.Cancel();
        try { CancelRequested?.Invoke(op.Invocation.ClientSession.ClientSessionId, op.Invocation.OperationId); } catch { }
        await op.TerminalGate.WaitAsync().ConfigureAwait(false);
        try { if (op.State < 4) await FinishFailedAsync(op, error).ConfigureAwait(false); }
        finally { op.TerminalGate.Release(); }
    }
    private static async Task FinishFailedAsync(Operation op, string error)
    {
        op.Result = new(false, Error: error); op.State = 5;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await op.Flow.Terminal(op.Invocation, PluginBrowserLoginTerminal.Cancelled, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch { op.State = 6; }
    }
    private Operation Find(PluginClientSessionContext client, string id)
    {
        lock (_sync) return _operations.TryGetValue(id, out var op) && SameClient(op, client) && !client.Lifetime.IsCancellationRequested
            ? op : throw new InvalidOperationException("browser_operation_not_found");
    }
    private static bool SameClient(Operation op, PluginClientSessionContext client) => op.Invocation.ClientSession.HostSessionId == client.HostSessionId && op.Invocation.ClientSession.ClientSessionId == client.ClientSessionId;
    private void Sweep()
    {
        Operation[] expired;
        lock (_sync)
        {
            expired = _operations.Values.Where(o => o.State < 4 && _time.GetElapsedTime(o.Created) >= TimeSpan.FromMinutes(15)).ToArray();
            foreach (string key in _operations.Where(p => p.Value.State >= 4 && _time.GetElapsedTime(p.Value.Created) >= TimeSpan.FromMinutes(20)).Select(p => p.Key).ToArray()) _operations.Remove(key);
        }
        foreach (var op in expired) _ = CancelAsync(op, "browser_operation_expired");
    }
    private static string SafeError(string? error) => error is { Length: > 0 and <= 64 } && error.All(c => char.IsAsciiLetterLower(c) || c is '_' or >= '0' and <= '9') ? error : "browser_validation_failed";
    private static bool Origin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo == "" && uri.AbsolutePath == "/" && uri.Query == "" && uri.Fragment == "" && uri.GetLeftPart(UriPartial.Authority) == value;
    private static void Validate(PluginBrowserLoginFlow flow)
    {
        if (flow.Id.Length is < 1 or > 64 || !flow.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || flow.Title.Length is < 1 or > 120
            || flow.FlowVersion.Length is < 1 or > 64 || flow.NavigationOrigins.Count is < 1 or > 16 || flow.PopupOrigins.Count > 8
            || flow.NavigationOrigins.Concat(flow.PopupOrigins).Any(o => !Origin(o))
            || !Uri.TryCreate(flow.StartUri, UriKind.Absolute, out var start) || start.Scheme != "https" || start.UserInfo != ""
            || !flow.NavigationOrigins.Contains(start.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal)
            || flow.Cookies.Count + flow.Storage.Count is < 1 or > 32) throw new InvalidDataException("browser_flow_invalid");
        if (flow.Cookies.Any(r => r.Domain.Length is < 1 or > 253 || r.Domain.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-')
            || !r.Path.StartsWith('/') || r.Path.Length > 256 || r.Name.Length is < 1 or > 80 || r.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            || flow.Storage.Any(r => !Origin(r.Origin) || !flow.NavigationOrigins.Contains(r.Origin) || r.Storage is not ("localStorage" or "sessionStorage")
                || r.Key.Length is < 1 or > 128 || r.JsonPath.Count > 8 || r.JsonPath.Any(p => p.Length is < 1 or > 128))
            || flow.Cookies.Distinct().Count() != flow.Cookies.Count
            || flow.Storage.Select(r => (r.Origin, r.Storage, r.Key)).Distinct().Count() != flow.Storage.Count) throw new InvalidDataException("browser_capture_rule_invalid");
    }
    private static void ValidateCapture(PluginBrowserLoginFlow flow, PluginBrowserCapture capture)
    {
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(capture)) > 48 * 1024 || capture.Cookies.Count > flow.Cookies.Count || capture.Storage.Count > flow.Storage.Count
            || capture.Cookies.Any(c => !flow.Cookies.Contains(new(c.Domain, c.Path, c.Name)))
            || capture.Storage.Any(s => !flow.Storage.Any(r => r.Origin == s.Origin && r.Storage == s.Storage && r.Key == s.Key))
            || capture.Cookies.Select(c => (c.Domain, c.Path, c.Name)).Distinct().Count() != capture.Cookies.Count
            || capture.Storage.Select(s => (s.Origin, s.Storage, s.Key)).Distinct().Count() != capture.Storage.Count) throw new InvalidDataException("browser_capture_invalid");
    }
    public void Dispose()
    {
        Operation[] active;
        lock (_sync) { if (_disposed) return; _disposed = true; _flows.Clear(); active = _operations.Values.Where(o => o.State < 4).ToArray(); }
        _sweep.Dispose();
        foreach (var op in active) _ = CancelAsync(op, "service_stopping");
    }
    private sealed class Registration(Action dispose) : IDisposable { private Action? _dispose = dispose; public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke(); }
}
