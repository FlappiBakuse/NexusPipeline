using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal sealed class ProcessIdentityScope : IDisposable
{
    [ThreadStatic] private static ProcessIdentityScope? _current;
    private readonly ProcessIdentityScope? _previous = _current;
    private readonly ProcessIdentityLease _identity;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed;

    internal ProcessIdentityScope(ProcessIdentityLease identity)
    { _identity = identity; _current = this; }

    internal static ProcessIdentity? ReadCurrent() => _current?._identity.ReadCurrent();

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("process_identity_scope_order");
        _disposed = true; _current = _previous;
    }
}
