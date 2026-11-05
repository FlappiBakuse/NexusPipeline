using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AttemptReadScope : IDisposable
{
    [ThreadStatic] private static AttemptReadScope? _current;
    private readonly AttemptReadScope? _previous = _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly ExecutionPlan _total;
    private readonly HashSet<JsonElement> _history;
    private readonly Dictionary<Type, object> _values = [];
    private JsonElement _root;
    private bool _disposed;

    internal AttemptReadScope(IReadOnlyList<JournalDocument> history, ExecutionPlan ownedTotal)
    {
        _total = ownedTotal; _history = history.Select(item => item.Root).ToHashSet();
        _current = this;
    }

    private bool Owns<T>(JsonElement journal, ExecutionPlan total)
    {
        if (typeof(T) != typeof(StepWriterState) && typeof(T) != typeof(StepResultState) && typeof(T) != typeof(ExecutionStepCompletion))
            throw new ArgumentException("attempt_read_type_not_immutable");
        return ReferenceEquals(total, _total) && _history.Contains(journal);
    }

    internal static bool TryRead<T>(JsonElement journal, ExecutionPlan total, out ImmutableArray<T> values)
    {
        values = default;
        if (_current is not { } scope || !scope.Owns<T>(journal, total)) return false;
        if (!scope._root.Equals(journal)) { scope._root = journal; scope._values.Clear(); }
        if (!scope._values.TryGetValue(typeof(T), out var saved)) return false;
        values = (ImmutableArray<T>)saved; return true;
    }

    internal static ImmutableArray<T> Remember<T>(JsonElement journal, ExecutionPlan total, ImmutableArray<T> values)
    {
        // The verifier owns this wire plan until it returns; only one original journal view is retained.
        if (values.Length <= 1024 && _current is { } scope && scope.Owns<T>(journal, total) && scope._root.Equals(journal))
            scope._values[typeof(T)] = values;
        return values;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("attempt_read_scope_order");
        _disposed = true; _current = _previous; _values.Clear(); _history.Clear();
    }
}
