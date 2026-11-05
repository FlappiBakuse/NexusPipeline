namespace NexusPipeline.Host.Control.Events;

internal sealed record ControlPersistentState(long CatalogRevision, long SettingsRevision,
    long PendingOperations, long Attempts, long CompletedRuns);

internal sealed class ControlEventStatistics(Func<long> readRevision, Func<ControlPersistentState> readState)
{
    private long _revision = -1;
    private ControlPersistentState? _state;

    internal ControlPersistentState Read()
    {
        var revision = readRevision();
        if (_revision == revision && _state is not null) return _state;
        var state = readState();
        if (readRevision() == revision) { _state = state; _revision = revision; }
        return state;
    }
}
