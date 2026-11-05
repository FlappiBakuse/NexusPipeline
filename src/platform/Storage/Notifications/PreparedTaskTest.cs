using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class PreparedTaskTest : IDisposable
{
    private readonly List<IDisposable> _held;
    private readonly ImmutableArray<NotificationCatalogEntry> _entries;
    internal TaskTestSnapshot Source { get; }
    internal NotificationDeliverySnapshot Delivery { get; }
    private bool _disposed;

    private PreparedTaskTest(TaskTestSnapshot source, NotificationDeliverySnapshot delivery,
        ImmutableArray<NotificationCatalogEntry> entries, List<IDisposable> held)
    { Source = source; Delivery = delivery; _entries = entries; _held = held; }

    internal static PreparedTaskTest Capture(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        Guid operation, string subject, long catalogRevision, Guid taskId, long taskRevision)
    {
        _ = TaskTestCodec.Identity(operation, subject, catalogRevision, taskId, taskRevision);
        var held = new List<IDisposable>();
        try
        {
            NotificationCatalogSnapshot catalog;
            ImmutableArray<NotificationDeliverySecret> secrets;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                var cursor = NotificationCatalogRows.ReadCurrent(connection, transaction) ?? throw new NotificationTaskException("notification_task_missing");
                if (cursor.Revision != catalogRevision) throw new NotificationTaskException("notification_revision_conflict");
                catalog = new NotificationCatalogCodec(instance, contracts).Read(cursor.Reference);
                NotificationCatalogRows.RequireTaskEntries(connection, transaction, catalog.Document.Tasks);
                var task = catalog.Catalog.Tasks.SingleOrDefault(task => task.Id == taskId) ?? throw new NotificationTaskException("notification_task_missing");
                if (task.Revision != taskRevision) throw new NotificationTaskException("notification_revision_conflict");
                var fields = ImmutableArray.CreateBuilder<NotificationDeliverySecret>();
                foreach (var field in Enum.GetValues<NotificationSecretField>())
                {
                    var reference = SecretFieldRows.ReadField(connection, transaction, SecretFieldKey.NotificationTask(instance.InstanceId, taskId, field));
                    fields.Add(new(field, reference, reference.SecretId is { } id ? SecretFieldStore.ReadPayload(connection, transaction, id) : null));
                }
                secrets = fields.ToImmutable(); transaction.Commit();
            }
            var blobs = new ImmutableBlobStore(instance, contracts);
            held.Add(blobs.OpenVerified(catalog.Reference));
            foreach (var entry in catalog.Document.Tasks) held.Add(blobs.OpenVerified(entry.Definition));
            var files = new SecretPayloadFiles(instance, new UserSecretProtector());
            foreach (var field in secrets) if (field.Payload is { } payload) held.Add(files.HoldVerified(payload));
            var codec = new TaskTestCodec(instance, database, contracts);
            var source = codec.Persist(new(TaskTestCodec.Format, 1, instance.InstanceId, operation, subject,
                catalogRevision, catalog.Reference, catalog.Document.Tasks.Single(task => task.TaskId == taskId), secrets));
            var delivery = new NotificationDeliveryCodec(instance, contracts).Persist(codec.Delivery(source));
            held.Add(blobs.OpenVerified(source.Reference)); held.Add(blobs.OpenVerified(delivery.Reference));
            return new(source, delivery, catalog.Document.Tasks, held);
        }
        catch { foreach (var item in held) item.Dispose(); throw; }
    }

    internal void AcceptInside(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset created, Guid generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = Source.Document;
        var cursor = NotificationCatalogRows.ReadCurrent(connection, transaction);
        if (cursor is null || cursor.Revision != source.ExpectedCatalogRevision || cursor.Reference != source.Catalog)
            throw new NotificationTaskException("notification_revision_conflict");
        NotificationCatalogRows.RequireTaskEntries(connection, transaction, _entries);
        foreach (var field in source.Secrets)
        {
            SecretFieldRows.RequireReference(connection, transaction, SecretFieldKey.NotificationTask(source.InstanceId, source.Task.TaskId, field.Field), field.Reference);
            NotificationDeliveryRows.RequirePayload(connection, transaction, field, allowRetired: false);
        }
        TaskTestRows.InsertInside(connection, transaction, Source, created);
        NotificationDeliveryRows.AcceptExplicitInside(connection, transaction, Delivery, created, generation);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var item in _held) item.Dispose();
        _held.Clear();
    }
}
