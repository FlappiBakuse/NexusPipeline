namespace NexusPipeline.Core.Desktop;

public sealed class NativeClientSessions(Guid instanceId) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _clients = [];
    private bool _disposed;

    public void Bind(Guid clientId, DesktopSessionIdentity desktop, Action requireFrontend, CancellationToken frontendLifetime,
        Action requireNative, CancellationToken nativeLifetime, Task? nativeExit = null)
    {
        if (instanceId == Guid.Empty || clientId == Guid.Empty || desktop.InstanceId != instanceId || desktop.ClientId == Guid.Empty ||
            desktop.SessionId == Guid.Empty || !frontendLifetime.CanBeCanceled || !nativeLifetime.CanBeCanceled)
            throw new UnauthorizedAccessException("native_frontend_binding_invalid");
        ArgumentNullException.ThrowIfNull(requireFrontend); ArgumentNullException.ThrowIfNull(requireNative);
        requireFrontend(); frontendLifetime.ThrowIfCancellationRequested(); requireNative(); nativeLifetime.ThrowIfCancellationRequested();
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.TryGetValue(clientId, out var prior))
            {
                if (prior.Desktop != desktop || prior.FrontendLifetime != frontendLifetime || prior.NativeLifetime != nativeLifetime)
                    throw new UnauthorizedAccessException("native_frontend_binding_changed");
                entry = prior;
            }
            else
            {
                if (_clients.Count >= 128) throw new InvalidOperationException("native_frontend_binding_limit");
                entry = new(desktop, requireFrontend, frontendLifetime, requireNative, nativeLifetime, nativeExit);
                _clients.Add(clientId, entry);
            }
        }
        entry.Observe(() => Remove(clientId, entry));
        try { entry.Require(); }
        catch { Remove(clientId, entry); throw; }
    }

    public NativeClientAccess Require(Guid clientId)
    {
        Entry entry;
        lock (_gate)
        {
            if (_disposed || !_clients.TryGetValue(clientId, out entry!)) throw new UnauthorizedAccessException("native_frontend_required");
        }
        void Current()
        {
            lock (_gate)
                if (_disposed || !_clients.TryGetValue(clientId, out var actual) || !ReferenceEquals(actual, entry))
                    throw new UnauthorizedAccessException("native_frontend_binding_ended");
            try { entry.Require(); }
            catch { Remove(clientId, entry); throw; }
        }
        Current(); return new(clientId, entry.Desktop, entry.Lifetime, entry.NativeExit, Current);
    }

    public void EndSession(Guid sessionId)
    {
        KeyValuePair<Guid, Entry>[] ended;
        lock (_gate)
        {
            ended = _clients.Where(item => item.Value.Desktop.SessionId == sessionId).ToArray();
            foreach (var item in ended) _clients.Remove(item.Key);
        }
        foreach (var item in ended) item.Value.End();
    }

    public void EndClient(Guid clientId, DesktopSessionIdentity desktop)
    {
        Entry? ended = null;
        lock (_gate)
            if (_clients.TryGetValue(clientId, out var entry))
            {
                if (entry.Desktop != desktop) throw new UnauthorizedAccessException("native_frontend_binding_changed");
                ended = entry; _clients.Remove(clientId);
            }
        ended?.End();
    }

    private void Remove(Guid id, Entry expected)
    {
        lock (_gate)
            if (_clients.TryGetValue(id, out var actual) && ReferenceEquals(actual, expected)) _clients.Remove(id);
        expected.End();
    }

    public void Dispose()
    {
        Entry[] ended;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; ended = _clients.Values.ToArray(); _clients.Clear();
        }
        foreach (var entry in ended) entry.End();
    }

    private sealed class Entry
    {
        private readonly Action _requireFrontend, _requireNative;
        private readonly CancellationTokenSource _lifetime;
        private CancellationTokenRegistration _retire;
        private int _observed, _ended;
        internal DesktopSessionIdentity Desktop { get; }
        internal CancellationToken FrontendLifetime { get; }
        internal CancellationToken NativeLifetime { get; }
        internal CancellationToken Lifetime { get; }
        internal Task NativeExit { get; }
        internal Entry(DesktopSessionIdentity desktop, Action requireFrontend, CancellationToken frontendLifetime,
            Action requireNative, CancellationToken nativeLifetime, Task? nativeExit)
        {
            Desktop = desktop; _requireFrontend = requireFrontend; FrontendLifetime = frontendLifetime;
            _requireNative = requireNative; NativeLifetime = nativeLifetime;
            NativeExit = nativeExit ?? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(frontendLifetime, nativeLifetime); Lifetime = _lifetime.Token;
        }
        internal void Observe(Action end)
        {
            if (Interlocked.Exchange(ref _observed, 1) != 0) return;
            _retire = Lifetime.Register(end);
            if (Volatile.Read(ref _ended) != 0) _retire.Unregister();
        }
        internal void Require()
        {
            Lifetime.ThrowIfCancellationRequested(); _requireFrontend(); _requireNative(); Lifetime.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this);
        }
        internal void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            try { _lifetime.Cancel(); }
            catch (AggregateException) { }
            finally { _retire.Unregister(); _lifetime.Dispose(); }
        }
    }
}

public sealed class NativeClientAccess
{
    public Guid ClientId { get; }
    public DesktopSessionIdentity Desktop { get; }
    public CancellationToken Lifetime { get; }
    public Task NativeExit { get; }
    private readonly Action _requireCurrent;
    internal NativeClientAccess(Guid clientId, DesktopSessionIdentity desktop, CancellationToken lifetime, Task nativeExit, Action requireCurrent)
    { ClientId = clientId; Desktop = desktop; Lifetime = lifetime; NativeExit = nativeExit; _requireCurrent = requireCurrent; }
    public void RequireCurrent() => _requireCurrent();
}
