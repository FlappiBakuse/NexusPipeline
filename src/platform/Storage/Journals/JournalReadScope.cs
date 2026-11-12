namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class JournalReadScope : IDisposable
{
    [ThreadStatic] private static JournalReadScope? _current;
    private readonly JournalReadScope? _previous = _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private JournalStore _store;
    private readonly Execution.AttemptReadRegistry? _attempts;
    private readonly JournalReadScope? _owner;
    private Guid _journalId;
    private IDisposable? _protection;
    private readonly bool _parents;
    private bool _disposed;
    private JournalReadScope Owner => _owner ?? this;

    private JournalReadScope(JournalStore store, Guid? journalId, Execution.AttemptReadRegistry? attempts = null)
    {
        _attempts = attempts ?? _previous?.Owner._attempts;
        _journalId = journalId ?? Guid.Empty; _parents = journalId is null;
        if (_parents && _previous is { _parents: true } parents && parents._store.Instance == store.Instance)
        {
            _owner = parents.Owner; _store = _owner._store;
        }
        else if (journalId is { } id && _previous is { } prior && prior.Matches(store, id) && prior.Owner._store.ProtectedJournalId == id)
            _store = prior.Owner._store;
        else
        {
            _store = store;
            if (journalId is { } selected && store.ProtectedJournalId is null) _protection = store.ProtectReadHistory(selected);
        }
        _current = this;
    }

    internal static JournalReadScope Hold(JournalStore store, Guid journalId) => new(store, journalId);

    internal static JournalReadScope HoldAttempt(RuntimeInstance instance, NexusPipeline.Platform.Serialization.ContractReader contracts,
        Sqlite.StateDatabase database, Guid journalId) => new(
            database.AttemptReads?.Find(journalId)?.Journals ?? new JournalStore(instance, contracts), journalId, database.AttemptReads);

    internal static JournalReadScope HoldParents(RuntimeInstance instance, NexusPipeline.Platform.Serialization.ContractReader contracts,
        Execution.AttemptReadRegistry? attempts = null) => new(new JournalStore(instance, contracts), null, attempts);

    internal static void SelectParent(RuntimeInstance instance, NexusPipeline.Platform.Serialization.ContractReader contracts, Guid journalId)
    {
        if (_current?.Owner is not { _parents: true } scope || scope._store.Instance != instance ||
            scope._journalId == journalId) return;
        scope._protection?.Dispose(); scope._protection = null; scope._journalId = Guid.Empty;
        if (scope._attempts?.Find(journalId) is { } active) scope._store = active.Journals;
        else
        {
            scope._store = new(instance, contracts);
            scope._protection = scope._store.ProtectReadHistory(journalId);
        }
        scope._journalId = journalId;
    }

    // Every reader uses the same embedded schemas; the instance and protected journal identify these bytes.
    private bool Matches(JournalStore store, Guid journalId) => Owner._journalId == journalId && Owner._store.Instance == store.Instance;

    internal static IReadOnlyList<JournalDocument>? Read(JournalStore requested, Guid journalId) =>
        _current is { } scope && scope.Matches(requested, journalId) ? scope.Owner._store.ReadProtectedHistory(journalId) : null;

    internal static JournalStore? WriteOwner(JournalStore requested, Guid journalId) =>
        _current is { } scope && scope.Matches(requested, journalId) && scope.Owner._store.ProtectedJournalId == journalId ? scope.Owner._store : null;

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("journal_read_scope_order");
        _disposed = true; _current = _previous;
        _protection?.Dispose();
    }
}
