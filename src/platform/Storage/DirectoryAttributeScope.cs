namespace NexusPipeline.Platform.Storage;

public sealed class DirectoryAttributeScope : IDisposable
{
    [ThreadStatic] private static DirectoryAttributeScope? _current;
    private readonly DirectoryAttributeScope? _previous = _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly DirectoryAttributeProtection _protection;
    private readonly bool _ownsProtection;
    private readonly Dictionary<string, FileAttributes> _facts;
    private readonly HashSet<object> _checks;
    private bool _disposed;

    private DirectoryAttributeScope(DirectoryAttributeProtection protection, bool ownsProtection)
    {
        _protection = protection;
        _ownsProtection = ownsProtection;
        _facts = _previous is { } prior && ReferenceEquals(prior._protection, protection)
            ? prior._facts : new(StringComparer.Ordinal);
        _checks = _previous is { } previous && ReferenceEquals(previous._protection, protection)
            ? previous._checks : new(ReferenceEqualityComparer.Instance);
        _current = this;
    }

    public static DirectoryAttributeScope Hold(RuntimeInstance instance) => _current is { } current &&
        StringComparer.OrdinalIgnoreCase.Equals(current._protection.RuntimeRoot, instance.RuntimeRoot)
        ? new(current._protection, false) : new(DirectoryAttributeProtection.Acquire(instance), true);

    internal static DirectoryAttributeScope HoldDirectories(IEnumerable<string> directories) =>
        new(DirectoryAttributeProtection.AcquireDirectories(directories), true);

    internal static DirectoryAttributeScope Enter(DirectoryAttributeProtection protection) => new(
        _current is { } current && protection.RuntimeRoot is { } root &&
        StringComparer.OrdinalIgnoreCase.Equals(current._protection.RuntimeRoot, root) ? current._protection : protection, false);

    internal static bool TryReadCurrent(string path, out FileAttributes attributes)
    {
        if (_current is { } scope)
        {
            if (scope._facts.TryGetValue(path, out attributes)) return true;
            if (scope._protection.TryReadCurrent(path, out attributes))
            { scope._facts.Add(path, attributes); return true; }
        }
        attributes = default;
        return false;
    }

    internal static bool FirstRead(object owner) => _current?._checks.Add(owner) ?? true;

    internal static void Refresh()
    {
        _current?._facts.Clear();
        _current?._checks.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        // A synchronous scope borrows protection without carrying thread-local state across an await.
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("directory_attributes_scope_order");
        _current = _previous;
        _disposed = true;
        if (_ownsProtection) _protection.Dispose();
    }
}
