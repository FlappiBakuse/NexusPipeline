using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationPublicationProof : IDisposable
{
    private readonly object _gate = new();
    private VerifiedHistory? _current;
    private bool _disposed;

    [SupportedOSPlatform("windows")]
    internal bool Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: NotificationPublicationInventory.Domains);
            using var transaction = DatabaseConnections.BeginRead(connection);
            var handle = connection.Handle!;
            var version = DatabaseVersion.Read(database, connection, NotificationPublicationInventory.Domains, transaction);
            if (_current is { } verified && ReferenceEquals(verified.Handle, handle) && verified.Version == version)
            {
                transaction.Commit();
                return true;
            }

            ReleaseHistory();
            var protection = new PublicationFiles(instance);
            try
            {
                (NotificationCatalogCursor? Cursor, bool HasPending) audit;
                using (BlobReadScope.Capture(instance, protection.Retain))
                    audit = new NotificationPublicationInventory(instance, database, contracts).VerifyInside(connection, transaction);
                transaction.Commit();
                if (protection.Complete)
                {
                    _current = new(handle, version, protection, audit.Cursor, audit.HasPending);
                    return true;
                }
            }
            catch { protection.Dispose(); throw; }
            protection.Dispose();
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal NotificationTaskCatalog PublishAppend(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        NotificationPublicationSnapshot publication, Func<NotificationTaskCatalog> publish)
    {
        const DatabaseDomain domains = NotificationPublicationInventory.Domains;
        using var writerScope = new DatabaseWriteScope(database.DatabasePath);
        var writer = DatabaseWriteScope.Borrow(database.DatabasePath)!;
        VerifiedHistory? history;
        DatabaseVersion version;
        long sequence, changes;
        lock (_gate)
        {
            using var snapshot = DatabaseConnections.BeginRead(writer, domains);
            version = DatabaseVersion.Read(database, writer, domains, snapshot);
            sequence = database.CommitSequence;
            changes = TotalChanges(writer, snapshot);
            // The pinned writer view and the audit view overlap on all four domains.
            Verify(instance, database, contracts);
            history = _current is { HasPending: false } trusted && !HasPublicationTriggers(writer, snapshot) ? trusted : null;
            snapshot.Commit();
        }

        var result = publish();
        if (history is null) return result;
        lock (_gate)
        {
            if (!ReferenceEquals(history, _current)) return result;
            using var snapshot = DatabaseConnections.BeginRead(writer, domains);
            // Acceptance, completion and the recovery readiness stamp use this handle. Other handles advance its data_version.
            var expectedChanges = 6 + publication.Before.Document.Tasks.Length + publication.After.Document.Tasks.Length;
            if (database.CommitSequence != sequence + 3 || TotalChanges(writer, snapshot) != changes + expectedChanges ||
                DatabaseVersion.Read(database, writer, domains, snapshot) != version)
            {
                ReleaseHistory();
                return result;
            }

            NotificationCatalogCursor cursor;
            using (BlobReadScope.Capture(instance, history.Files.Retain))
                cursor = new NotificationPublicationInventory(instance, database, contracts)
                    .VerifyAppend(writer, snapshot, publication.Reference, history.Cursor);
            if (!history.Files.Complete)
            {
                ReleaseHistory();
                return result;
            }
            using var reader = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: domains);
            using var read = DatabaseConnections.BeginRead(reader);
            var current = DatabaseVersion.Read(database, reader, domains, read);
            read.Commit();
            snapshot.Commit();
            _current = new(reader.Handle!, current, history.Files, cursor, false);
            return result;
        }
    }

    private static long TotalChanges(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = DatabaseConnections.Command(connection, transaction, "SELECT total_changes();");
        return (long)command.ExecuteScalar()!;
    }

    private static bool HasPublicationTriggers(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = DatabaseConnections.Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM main.sqlite_schema WHERE type='trigger' AND tbl_name IN ('operations','recovery_index_state'))
            OR EXISTS(SELECT 1 FROM notifications.sqlite_schema WHERE type='trigger' AND tbl_name IN
                ('notification_publications','notification_publication_outcomes','notification_catalog_current','notification_catalog_tasks'));
            """);
        return (long)command.ExecuteScalar()! != 0;
    }

    internal void Invalidate()
    {
        lock (_gate) ReleaseHistory();
    }

    private void ReleaseHistory()
    {
        _current?.Files.Dispose();
        _current = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseHistory();
        }
    }

    private sealed record VerifiedHistory(SQLitePCL.sqlite3 Handle, DatabaseVersion Version, PublicationFiles Files,
        NotificationCatalogCursor? Cursor, bool HasPending);

    private sealed class PublicationFiles : IDisposable
    {
        private readonly string _directory;
        private readonly Dictionary<string, IDisposable> _retained = new(StringComparer.OrdinalIgnoreCase);
        private StableDirectoryLease? _namespace;
        internal bool Complete { get; private set; } = true;

        internal PublicationFiles(RuntimeInstance instance)
        {
            _directory = Path.Combine(instance.RuntimeRoot, "blobs", "plans");
            if (Directory.Exists(_directory)) _namespace = StableDirectoryLease.Acquire(_directory);
        }

        internal void Retain(string path)
        {
            if (!Complete || _retained.ContainsKey(path)) return;
            if (_namespace is null || _retained.Count == BlobReadScope.MaximumReferences ||
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), _directory))
            {
                Complete = false;
                Dispose();
                return;
            }
            _retained.Add(path, BlobReadStream.Retain(path));
        }

        public void Dispose()
        {
            foreach (var lease in _retained.Values) lease.Dispose();
            _retained.Clear();
            _namespace?.Dispose();
            _namespace = null;
        }
    }
}
