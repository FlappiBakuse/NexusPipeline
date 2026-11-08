namespace NexusPipeline.Extensibility.Lifecycle;

/// <summary>Reads the actual native desktop session without creating one from a browser request.</summary>
public interface IExtensionDesktop
{
    /// <summary>Reads bounded native window observations for a declared desktop editor; observations grant no target access.</summary>
    ValueTask<IReadOnlyList<DesktopWindow>> ReadWindowsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<IReadOnlyList<DesktopWindow>>(new NotSupportedException("desktop_window_inventory_unavailable"));
    /// <summary>Null means no native desktop handshake has established a current session.</summary>
    ValueTask<Guid?> ReadSessionAsync(CancellationToken cancellationToken);
    /// <summary>Reads current client availability without authorizing an interactive operation.</summary>
    ValueTask<DesktopClientState> ReadClientAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new DesktopClientState(false, false));
    /// <summary>Requires the actual current desktop frontend call bound to its verified native client and original grant lifetime.</summary>
    ValueTask RequireClientAsync(CancellationToken cancellationToken) => ValueTask.FromException(new NotSupportedException("native_frontend_unavailable"));
    /// <summary>Captures only declared login values in an isolated native session and returns after its window and session are cleaned.</summary>
    ValueTask<BrowserLoginCapture> CaptureAsync(BrowserLoginPolicy policy, CancellationToken cancellationToken) =>
        ValueTask.FromException<BrowserLoginCapture>(new NotSupportedException("browser_login_unavailable"));
    /// <summary>Keeps the isolated window available after a rejected candidate and cleans it after validation succeeds.</summary>
    ValueTask<BrowserLoginCapture> CaptureValidatedAsync(BrowserLoginPolicy policy,
        Func<BrowserLoginCapture, CancellationToken, ValueTask<bool>> validate, CancellationToken cancellationToken) =>
        ValueTask.FromException<BrowserLoginCapture>(new NotSupportedException("browser_login_validation_unavailable"));
}

/// <summary>Native binding and available browser capture are distinct current Host facts; neither grants execution authority.</summary>
public sealed record DesktopClientState(bool IsBound, bool CanCapture);

/// <summary>Display metadata; execution separately admits the actual target and original process lifetime.</summary>
public sealed record DesktopWindow(long Handle, int ProcessId, string Title, string Executable, DateTimeOffset StartedAtUtc);
