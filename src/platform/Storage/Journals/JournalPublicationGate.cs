namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class JournalPublicationGate : IDisposable
{
    private readonly Mutex _mutex;

    private JournalPublicationGate(string name, Guid journalId, IJournalPublicationObserver? observer)
    {
        _mutex = new(false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        try
        {
            try
            {
                if (observer is not null)
                {
                    if (_mutex.WaitOne(0)) return;
                    observer.ReadBlocked(journalId);
                }
                if (!_mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("journal_publication_busy");
            }
            catch (AbandonedMutexException)
            {
                // Acquiring an abandoned lock never makes an incomplete revision valid.
            }
        }
        catch { _mutex.Dispose(); throw; }
    }

    internal static JournalPublicationGate Acquire(RuntimeInstance instance, Guid journalId, IJournalPublicationObserver? reader = null) =>
        new(Name(instance, journalId), journalId, reader);

    internal static string Name(RuntimeInstance instance, Guid journalId)
    {
        if (journalId == Guid.Empty) throw new ArgumentException("Journal identity is required");
        var identity = PhysicalObjectIdentity.Read(instance.RuntimeRoot);
        return $"NexusJournal-{identity.VolumeSerial}-{identity.FileId}-{journalId:N}";
    }

    public void Dispose()
    {
        try { _mutex.ReleaseMutex(); }
        finally { _mutex.Dispose(); }
    }
}

internal interface IJournalPublicationObserver
{
    void RevisionCreated(string path);
    void ReadBlocked(Guid journalId) { }
}
