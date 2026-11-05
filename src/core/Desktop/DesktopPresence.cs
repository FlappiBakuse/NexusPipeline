namespace NexusPipeline.Core.Desktop;

public sealed class DesktopPresence
{
    private readonly object _gate = new();
    private Guid? _current;

    public Guid? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Begin(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("desktop_session_identity_empty");
        lock (_gate) _current = sessionId;
    }

    public void End(Guid sessionId)
    {
        lock (_gate) if (_current == sessionId) _current = null;
    }
}
