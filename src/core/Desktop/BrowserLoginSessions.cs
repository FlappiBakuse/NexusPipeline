using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Core.Desktop;

public sealed record BrowserLoginOffer(Guid CaptureId, Guid Nonce, Guid ClientId, string ExtensionId, Guid ActivationId,
    Guid OperationId, BrowserLoginPolicy Policy, bool IsCanceled);

public sealed class BrowserLoginSessions(TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<DesktopSessionIdentity, DateTimeOffset> _peers = [];
    private readonly Dictionary<Guid, Entry> _entries = [];
    private bool _stopping;
    private long _pollSequence;

    public bool CanCapture(NativeClientAccess client)
    {
        client.RequireCurrent();
        lock (_gate) return !_stopping && _peers.TryGetValue(client.Desktop, out var observed) &&
            _clock.GetUtcNow() - observed < TimeSpan.FromSeconds(5);
    }

    public IReadOnlyList<BrowserLoginOffer> Poll(DesktopSessionIdentity native)
    {
        lock (_gate)
        {
            if (!_peers.ContainsKey(native) && _peers.Count >= 128) throw new IOException("browser_login_peer_limit");
            _peers[native] = _clock.GetUtcNow();
            var offers = new List<BrowserLoginOffer>();
            foreach (var entry in _entries.Values.Where(entry => entry.Client.Desktop == native && !entry.Completion.Task.IsCompleted)
                .OrderBy(entry => entry.LastOffered).Take(1))
            {
                entry.Offered = true; entry.LastOffered = checked(++_pollSequence);
                offers.Add(entry.Offer with { IsCanceled = entry.Cancellation.IsCancellationRequested || _stopping });
            }
            return offers.AsReadOnly();
        }
    }

    public async ValueTask<BrowserLoginMaterial> CaptureAsync(NativeClientAccess client, string extensionId, Guid activationId,
        Guid operationId, BrowserLoginPolicy policy, Action requireCurrent, CancellationToken callbackLifetime, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(extensionId) || activationId == Guid.Empty || operationId == Guid.Empty || !callbackLifetime.CanBeCanceled)
            throw new UnauthorizedAccessException("browser_login_callback_unbound");
        requireCurrent(); client.RequireCurrent(); cancellation.ThrowIfCancellationRequested(); callbackLifetime.ThrowIfCancellationRequested();
        var entry = new Entry(client, extensionId, activationId, operationId, policy, requireCurrent, callbackLifetime, cancellation);
        lock (_gate)
        {
            if (!CanCapture(client)) { entry.Dispose(); throw new IOException("browser_login_peer_unavailable"); }
            if (_entries.Count >= 8 || _entries.Values.Any(item => item.Client.ClientId == client.ClientId))
            { entry.Dispose(); throw new IOException("browser_login_capture_busy"); }
            _entries.Add(entry.Offer.CaptureId, entry);
        }
        using var retire = entry.Cancellation.Token.Register(() => Cancel(entry));
        _ = ObserveExitAsync(entry);
        try { return await entry.Completion.Task.ConfigureAwait(false); }
        finally
        {
            lock (_gate) _entries.Remove(entry.Offer.CaptureId);
            entry.Finished.TrySetResult(); entry.Dispose();
        }
    }

    public void Complete(DesktopSessionIdentity native, Guid captureId, Guid nonce, string status,
        IReadOnlyDictionary<string, SecretValue> values)
    {
        if (status is not ("completed" or "canceled" or "failed")) throw new ArgumentException("browser_login_status_invalid");
        lock (_gate)
        {
            if (!_entries.TryGetValue(captureId, out var entry) || entry.Client.Desktop != native || entry.Offer.Nonce != nonce || !entry.Offered)
                throw new UnauthorizedAccessException("browser_login_capture_unbound");
            if (entry.Completion.Task.IsCompleted) return;
            if (entry.Cancellation.IsCancellationRequested || _stopping) { entry.Completion.TrySetCanceled(); return; }
            try { entry.Client.RequireCurrent(); entry.RequireCurrent(); }
            catch (Exception error) when (error is UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException or IOException)
            { entry.Completion.TrySetException(new UnauthorizedAccessException("browser_login_capture_retired")); return; }
            if (status != "completed") { entry.Completion.TrySetException(new IOException("browser_login_" + status)); return; }
            if (!BrowserValueRules.IsReady(entry.Offer.Policy, values))
            { entry.Completion.TrySetException(new IOException("browser_login_capture_incomplete")); return; }
            var material = new BrowserLoginMaterial(values);
            if (!entry.Completion.TrySetResult(material)) material.Dispose();
        }
    }

    private void Cancel(Entry entry)
    {
        lock (_gate)
            if (!entry.Offered) entry.Completion.TrySetCanceled();
    }

    private static async Task ObserveExitAsync(Entry entry)
    {
        var ended = await Task.WhenAny(entry.Client.NativeExit, entry.Finished.Task).ConfigureAwait(false);
        if (ended == entry.Client.NativeExit && entry.Client.NativeExit.IsCompletedSuccessfully)
            entry.Completion.TrySetException(new IOException("browser_login_native_exited"));
    }

    public async ValueTask StopAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Entry[] pending;
        lock (_gate)
        {
            _stopping = true; pending = _entries.Values.ToArray(); _peers.Clear();
            foreach (var entry in pending) entry.Cancellation.Cancel();
        }
        try { await Task.WhenAll(pending.Select(entry => entry.Finished.Task)).WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { throw new IOException("browser_login_cleanup_unconfirmed"); }
    }

    public ValueTask DisposeAsync() => StopAsync(TimeSpan.FromSeconds(10));

    private sealed class Entry : IDisposable
    {
        internal NativeClientAccess Client { get; }
        internal BrowserLoginOffer Offer { get; }
        internal Action RequireCurrent { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal TaskCompletionSource<BrowserLoginMaterial> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Offered { get; set; }
        internal long LastOffered { get; set; }
        internal Entry(NativeClientAccess client, string extensionId, Guid activationId, Guid operationId, BrowserLoginPolicy policy,
            Action requireCurrent, CancellationToken callbackLifetime, CancellationToken cancellation)
        {
            Client = client; RequireCurrent = requireCurrent;
            Offer = new(Guid.NewGuid(), Guid.NewGuid(), client.ClientId, extensionId, activationId, operationId, policy, false);
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(client.Lifetime, callbackLifetime, cancellation);
            Cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        }
        public void Dispose() => Cancellation.Dispose();
    }
}
