namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class JournalReadScope : IDisposable
{
    [ThreadStatic] private static JournalReadScope? _current;
    private readonly JournalReadScope? _previous = _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly JournalStore _store;
    private readonly JournalReadScope? _owner;
    private Guid _journalId;
    private IDisposable? _protection;
    private readonly bool _parents;
    private bool _disposed;
    private JournalReadScope Owner => _owner ?? this;

    private JournalReadScope(JournalStore store, Guid? journalId)
    {
        _journalId = journalId ?? Guid.Empty; _parents = journalId is null;
        if (_parents && _previous is { _parents: true } parents && parents._store.Instance == store.Instance &&
            ReferenceEquals(parents._store.Contracts, store.Contracts))
        {
            _owner = parents.Owner; _store = _owner._store;
        }
        else if (journalId is { } id && _previous is { } prior && prior.Matches(store, id) && prior._store.ProtectedJournalId == id)
            _store = prior._store;
        else
        {
            _store = store;
            if (journalId is { } selected && store.ProtectedJournalId is null) _protection = store.ProtectReadHistory(selected);
        }
        _current = this;
    }

    internal static JournalReadScope Hold(JournalStore store, Guid journalId) => new(store, journalId);

    internal static JournalReadScope HoldParents(RuntimeInstance instance, NexusPipeline.Platform.Serialization.ContractReader contracts) =>
        new(new JournalStore(instance, contracts), null);

    internal static void SelectParent(RuntimeInstance instance, NexusPipeline.Platform.Serialization.ContractReader contracts, Guid journalId)
    {
        if (_current?.Owner is not { _parents: true } scope || scope._store.Instance != instance ||
            !ReferenceEquals(scope._store.Contracts, contracts) || scope._journalId == journalId) return;
        scope._protection?.Dispose(); scope._protection = null; scope._journalId = Guid.Empty;
        scope._protection = scope._store.ProtectReadHistory(journalId);
        scope._journalId = journalId;
    }

    private bool Matches(JournalStore store, Guid journalId) => Owner._journalId == journalId &&
        _store.Instance == store.Instance && ReferenceEquals(_store.Contracts, store.Contracts);

    internal static IReadOnlyList<JournalDocument>? Read(JournalStore requested, Guid journalId) =>
        _current is { } scope && scope.Matches(requested, journalId) ? scope._store.ReadProtectedHistory(journalId) : null;

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("journal_read_scope_order");
        _disposed = true; _current = _previous;
        _protection?.Dispose();
    }
}
