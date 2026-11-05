using System.Runtime.CompilerServices;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class ProjectSaveFlights
{
    private static readonly ConditionalWeakTable<InstanceLease, ProjectSaveFlights> Owners = new();
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    internal static ProjectSaveFlights For(InstanceLease lease) => Owners.GetValue(lease, _ => new());

    internal Callback Begin(Guid operation, string subject, string digest)
    {
        lock (gate)
        {
            var entry = Find(operation, subject, digest, active: true);
            if (entry.Closing) throw new IOException("project_operation_settling");
            entry.Active++;
            return new(this, operation, entry);
        }
    }

    internal Closing Close(Guid operation, string subject, string digest)
    {
        Entry entry;
        lock (gate)
        {
            entry = Find(operation, subject, digest, active: false);
            entry.Closing = true; entry.Closers++;
        }
        try { entry.Cancellation.Cancel(); } catch (AggregateException) { }
        return new(this, operation, entry);
    }

    private Entry Find(Guid operation, string subject, string digest, bool active)
    {
        if (entries.TryGetValue(operation, out var entry))
        {
            if (entry.Subject != subject || entry.Digest != digest) throw new Core.Operations.OperationConflictException();
            return entry;
        }
        if (entries.Count >= 128) throw new IOException("project_operations_full");
        entry = new(subject, digest, active); entries.Add(operation, entry); return entry;
    }

    private void Release(Guid operation, Entry entry, bool callback)
    {
        lock (gate)
        {
            if (callback)
            {
                entry.Active--;
                if (entry.Active == 0) entry.Drained.TrySetResult();
            }
            else entry.Closers--;
            if (entry.Active == 0 && entry.Closers == 0)
            { entries.Remove(operation); entry.Cancellation.Dispose(); }
        }
    }

    internal sealed class Entry
    {
        internal string Subject { get; }
        internal string Digest { get; }
        internal int Active { get; set; }
        internal int Closers { get; set; }
        internal bool Closing { get; set; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Entry(string subject, string digest, bool active)
        {
            Subject = subject; Digest = digest;
            if (!active) Drained.SetResult();
        }
    }

    internal sealed class Callback(ProjectSaveFlights owner, Guid operation, Entry entry) : IDisposable
    {
        private int disposed;
        internal CancellationToken Cancellation => entry.Cancellation.Token;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(operation, entry, true); }
    }

    internal sealed class Closing(ProjectSaveFlights owner, Guid operation, Entry entry) : IDisposable
    {
        private int disposed;
        internal Task DrainAsync(CancellationToken cancellation) => entry.Drained.Task.WaitAsync(cancellation);
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(operation, entry, false); }
    }
}
