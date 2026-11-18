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
    private readonly AttemptReadMemo? _memo;
    private readonly Dictionary<JsonElement, JournalDocument> _history = [];
    private readonly HashSet<JsonElement> _originals = [];
    private readonly Dictionary<(AttemptJournalView, Type), object> _values = [];
    private readonly HashSet<AttemptJournalView> _owned = [];
    private readonly HashSet<JsonElement> _participants = [];
    private readonly Dictionary<JsonElement, ExecutionInvocation> _invocations = [];
    private readonly Dictionary<AttemptJournalView, AttemptJournalView> _projections = [];
    private readonly Dictionary<AttemptJournalView, IReadOnlyList<AttemptJournalView>> _views = [];
    private int _viewBytes;
    private bool _disposed;

    internal AttemptReadScope(IReadOnlyList<JournalDocument> history, ExecutionPlan ownedTotal, AttemptReadMemo? memo = null)
    {
        _total = ownedTotal; _memo = memo;
        foreach (var document in history) _history.TryAdd(document.Root, document);
        _current = this;
    }

    private bool Owns<T>(AttemptJournalView journal, ExecutionPlan total)
    {
        if (typeof(T) != typeof(StepWriterState) && typeof(T) != typeof(StepResultState) &&
            typeof(T) != typeof(ExecutionStepCompletion) && typeof(T) != typeof(ExecutionInvocation))
            throw new ArgumentException("attempt_read_type_not_immutable");
        return ReferenceEquals(total, _total) && Owns(journal);
    }

    private bool Owns(AttemptJournalView journal)
    {
        if (!journal.IsOriginal || !_history.ContainsKey(journal.Root)) return _owned.Contains(journal);
        if (!_originals.Contains(journal.Root))
        {
            if (_originals.Count == 2) Clear();
            _originals.Add(journal.Root);
            _participants.UnionWith(journal.Participants);
            Register(journal);
        }
        return true;
    }

    private bool Register(AttemptJournalView value)
    {
        if (_owned.Contains(value)) return true;
        if (_owned.Count >= 32) return false;
        var bytes = value.RetainedSelectionBytes;
        if (bytes > 4194304 - _viewBytes) return false;
        _viewBytes += bytes; _owned.Add(value);
        return true;
    }

    internal static bool TryInvocation(JsonElement participant, out ExecutionInvocation value)
    {
        value = null!;
        return _current is { } scope && scope._invocations.TryGetValue(participant, out value!);
    }

    internal static ExecutionInvocation RememberInvocation(JsonElement participant, ExecutionInvocation value)
    {
        if (_current is { } scope && scope._participants.Contains(participant)) scope._invocations[participant] = value;
        return value;
    }

    internal static bool TryProjection(AttemptJournalView journal, out AttemptJournalView value)
    {
        value = default;
        if (_current is not { } scope || !scope.Owns(journal)) return false;
        if (journal.IsOriginal && scope._memo?.TryProjection(journal.Root, scope._total, out value) == true)
        { scope.Register(value); return true; }
        return scope._projections.TryGetValue(journal, out value);
    }

    internal static AttemptJournalView RememberProjection(AttemptJournalView journal, AttemptJournalView value)
    {
        if (_current is { } scope && scope.Owns(journal) && scope.Register(value))
        {
            scope._projections[journal] = value;
            if (journal.IsOriginal) scope._memo?.RememberProjection(scope._history[journal.Root], scope._total, value);
        }
        return value;
    }

    internal static bool TryViews(AttemptJournalView journal, out IReadOnlyList<AttemptJournalView> value)
    {
        value = null!;
        if (_current is not { } scope || !scope.Owns(journal)) return false;
        if (journal.IsOriginal && scope._memo?.TryViews(journal.Root, scope._total, out value) == true)
        { foreach (var view in value) scope.Register(view); return true; }
        return scope._views.TryGetValue(journal, out value!);
    }

    internal static IReadOnlyList<AttemptJournalView> RememberViews(AttemptJournalView journal, IReadOnlyList<AttemptJournalView> value)
    {
        if (_current is { } scope && scope.Owns(journal) && value.Count <= 32 && value.All(scope.Register))
        {
            scope._views[journal] = value;
            if (journal.IsOriginal) scope._memo?.RememberViews(scope._history[journal.Root], scope._total, value);
        }
        return value;
    }

    internal static bool TryRead<T>(AttemptJournalView journal, ExecutionPlan total, out ImmutableArray<T> values)
    {
        values = default;
        if (_current is not { } scope || !scope.Owns<T>(journal, total)) return false;
        if (journal.IsOriginal && scope._memo?.TryRead(journal.Root, total, out values) == true) return true;
        if (!scope._values.TryGetValue((journal, typeof(T)), out var saved)) return false;
        values = (ImmutableArray<T>)saved; return true;
    }

    internal static ImmutableArray<T> Remember<T>(AttemptJournalView journal, ExecutionPlan total, ImmutableArray<T> values)
    {
        // Retained views derive only from the protected originals and the same owned wire plan.
        if (values.Length <= 1024 && _current is { } scope && scope.Owns<T>(journal, total))
        {
            scope._values[(journal, typeof(T))] = values;
            if (journal.IsOriginal) scope._memo?.Remember(scope._history[journal.Root], total, values);
        }
        return values;
    }

    private void Clear()
    {
        _values.Clear(); _originals.Clear(); _owned.Clear(); _participants.Clear(); _invocations.Clear(); _projections.Clear(); _views.Clear(); _viewBytes = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("attempt_read_scope_order");
        _disposed = true; _current = _previous; Clear(); _history.Clear();
    }
}
