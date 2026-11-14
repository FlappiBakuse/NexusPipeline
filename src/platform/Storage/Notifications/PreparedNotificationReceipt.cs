using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class PreparedNotificationReceipt : IDisposable
{
    private readonly List<IDisposable> _held;
    private bool _disposed;
    private readonly ImmutableArray<NotificationCatalogEntry> _catalogTasks;
    internal NotificationReceiptSnapshot Snapshot { get; }

    private PreparedNotificationReceipt(NotificationReceiptSnapshot snapshot, ImmutableArray<NotificationCatalogEntry> catalogTasks, List<IDisposable> held)
    {
        Snapshot = snapshot; _catalogTasks = catalogTasks; _held = held;
    }

    internal static PreparedNotificationReceipt Capture(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, ExecutionNotificationSource source)
    {
        var blobs = new ImmutableBlobStore(instance, contracts);
        var held = new List<IDisposable>();
        try
        {
            NotificationCatalogCursor? cursor;
            NexusPipeline.Core.Records.RecordAsset? screenshot;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System | DatabaseDomain.Content))
                cursor = NotificationCatalogRows.ReadCurrent(connection, null);
            var codec = new NotificationCatalogCodec(instance, contracts);
            var catalog = cursor is null ? codec.Persist(NotificationTaskCatalog.Empty) : codec.Read(cursor.Reference);
            if (catalog.Catalog.Revision != (cursor?.Revision ?? 0)) throw new IOException("notification_catalog_cursor_unbound");
            held.Add(blobs.OpenVerified(source.TotalPlan)); held.Add(blobs.OpenVerified(source.DefinitionBundle));
            held.Add(blobs.OpenVerified(catalog.Reference));
            foreach (var task in catalog.Document.Tasks) held.Add(blobs.OpenVerified(task.Definition));
            var selected = source.Routes.SelectMany(route => route.Selection.TaskIds).ToHashSet();
            var tasks = ImmutableArray.CreateBuilder<ReceiptTaskSecrets>();
            var files = new SecretPayloadFiles(instance, new UserSecretProtector());
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System | DatabaseDomain.Content))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                if (NotificationCatalogRows.ReadCurrent(connection, transaction) != cursor)
                    throw new NotificationTaskException("notification_revision_conflict");
                NotificationCatalogRows.RequireTaskEntries(connection, transaction, catalog.Document.Tasks);
                foreach (var task in catalog.Catalog.Tasks.Where(task => task.IsEnabled && selected.Contains(task.Id))
                    .OrderBy(task => task.Id.ToString("D"), StringComparer.Ordinal))
                {
                    var fields = ImmutableArray.CreateBuilder<NotificationDeliverySecret>();
                    foreach (var field in Enum.GetValues<NotificationSecretField>())
                    {
                        var reference = SecretFieldRows.ReadField(connection, transaction,
                            SecretFieldKey.NotificationTask(instance.InstanceId, task.Id, field));
                        var payload = reference.SecretId is { } id ? SecretFieldStore.ReadPayload(connection, transaction, id) : null;
                        fields.Add(new(field, reference, payload));
                    }
                    tasks.Add(new(task.Id, fields.ToImmutable()));
                }
                screenshot = NotificationReceiptAssets.ReadPublished(connection, transaction, source.Receipt.Facts.RunId);
                transaction.Commit();
            }
            foreach (var payload in tasks.SelectMany(task => task.Secrets).Select(field => field.Payload).OfType<SecretPayloadIdentity>().Distinct())
                held.Add(files.HoldVerified(payload));
            if (screenshot is { } asset)
                held.Add(blobs.OpenVerified(new($"blobs/content/{asset.Sha256}.bin", asset.SizeBytes, asset.Sha256)));
            var snapshot = new NotificationReceiptCodec(instance, database, contracts).Persist(source, catalog, tasks.ToImmutable(), screenshot);
            held.Add(blobs.OpenVerified(snapshot.Reference));
            return new(snapshot, catalog.Document.Tasks, held);
        }
        catch { foreach (var item in held) item.Dispose(); throw; }
    }

    internal BlobReference Accept(StateDatabase database, InstanceLease lease)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (database.InstanceId != Snapshot.Document.InstanceId || lease.Instance.InstanceId != database.InstanceId ||
            database.RuntimeRoot != lease.Instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(database.RuntimeRoot, "state", "runtime.db"))) throw NotificationReceiptCodec.Invalid();
        return lease.PublishExecution(() => database.Write((connection, transaction) =>
            NotificationReceiptRows.AcceptInside(connection, transaction, Snapshot, _catalogTasks, DateTimeOffset.UtcNow)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var item in _held) item.Dispose();
        _held.Clear();
    }
}
