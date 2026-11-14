using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class PreparedNotificationSelections : IDisposable
{
    private readonly StateDatabase _database;
    private readonly NotificationCatalogCursor? _cursor;
    private readonly ImmutableArray<NotificationCatalogEntry> _tasks;
    private readonly List<IDisposable> _held;
    private bool _disposed;

    private PreparedNotificationSelections(StateDatabase database, NotificationCatalogCursor? cursor,
        ImmutableArray<NotificationCatalogEntry> tasks, List<IDisposable> held)
    {
        _database = database; _cursor = cursor; _tasks = tasks; _held = held;
    }

    internal static PreparedNotificationSelections Capture(StateDatabase database)
    {
        NotificationCatalogCursor? cursor;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications))
            cursor = NotificationCatalogRows.ReadCurrent(connection, null);
        var held = new List<IDisposable>();
        try
        {
            var tasks = ImmutableArray<NotificationCatalogEntry>.Empty;
            if (cursor is not null)
            {
                var instance = new InstanceRoot(new()).OpenExisting(database.RuntimeRoot);
                if (instance.InstanceId != database.InstanceId) throw new IOException("notification_selection_instance_unbound");
                var snapshot = new NotificationCatalogCodec(instance, new()).Read(cursor.Reference);
                if (snapshot.Catalog.Revision != cursor.Revision) throw new IOException("notification_catalog_cursor_unbound");
                tasks = snapshot.Document.Tasks;
                var blobs = new ImmutableBlobStore(instance, new());
                held.Add(blobs.OpenVerified(cursor.Reference));
                foreach (var task in tasks) held.Add(blobs.OpenVerified(task.Definition));
            }
            return new(database, cursor, tasks, held);
        }
        catch { foreach (var item in held) item.Dispose(); throw; }
    }

    internal void RequireChanges(AutomationCatalog before, AutomationChanges changes)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireChangesInside(connection, transaction, before, changes);
        transaction.Commit();
    }

    internal void RequireChangesInside(SqliteConnection connection, SqliteTransaction transaction,
        AutomationCatalog before, AutomationChanges changes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (NotificationCatalogRows.ReadCurrent(connection, transaction) != _cursor)
            throw new NotificationTaskException("notification_revision_conflict");
        NotificationCatalogRows.RequireTaskEntries(connection, transaction, _tasks);
        var known = _tasks.Select(item => item.TaskId).ToHashSet();
        var bindings = before.Bindings.ToDictionary(item => item.Id);
        foreach (var candidate in changes.Bindings)
            _ = NotificationTaskPolicy.ValidateSelectionIds(known, new(candidate.NotificationTaskIds),
                bindings.TryGetValue(candidate.Id, out var previous) ? new(previous.NotificationTaskIds) : null);
        var queues = before.Queues.ToDictionary(item => item.Id);
        foreach (var candidate in changes.Queues)
            _ = NotificationTaskPolicy.ValidateSelectionIds(known, new(candidate.NotificationTaskIds),
                queues.TryGetValue(candidate.Id, out var previous) ? new(previous.NotificationTaskIds) : null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var item in _held) item.Dispose();
        _held.Clear();
    }
}
