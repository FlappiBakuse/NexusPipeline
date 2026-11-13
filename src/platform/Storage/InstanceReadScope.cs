using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage;

internal sealed class InstanceReadScope : IDisposable
{
    [ThreadStatic] private static InstanceReadScope? _current;
    private readonly InstanceReadScope? _previous = _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly RuntimeInstance _instance;
    private readonly StableDirectoryLease? _directory;
    private readonly FileStream? _identity;
    private bool _disposed;

    private InstanceReadScope(RuntimeInstance instance, ContractReader contracts)
    {
        _instance = instance;
        if (_previous?._instance == instance) { _current = this; return; }
        _directory = StableDirectoryLease.Acquire(instance.RuntimeRoot);
        try
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "instance.json"));
            _identity = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                _ = PhysicalObjectIdentity.Read(_identity.SafeFileHandle);
                if (new InstanceRoot(contracts).OpenExisting(instance.RuntimeRoot) != instance)
                    throw new IOException("runtime_identity_changed");
                _current = this;
            }
            catch { _identity.Dispose(); throw; }
        }
        catch { _directory.Dispose(); throw; }
    }

    internal static InstanceReadScope Hold(RuntimeInstance instance, ContractReader contracts) => new(instance, contracts);

    internal static RuntimeInstance? Read(string root)
    {
        if (_current is not { } scope || !StringComparer.OrdinalIgnoreCase.Equals(root, scope._instance.RuntimeRoot)) return null;
        // The identity denies writes/deletion and the directory leases pin every ancestor name.
        return scope._instance;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("instance_read_scope_order");
        _disposed = true; _current = _previous;
        try { _identity?.Dispose(); } finally { _directory?.Dispose(); }
    }
}
