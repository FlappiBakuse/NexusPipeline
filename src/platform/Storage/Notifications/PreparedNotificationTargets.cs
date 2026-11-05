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
internal sealed class PreparedNotificationTargets : IDisposable
{
    private readonly List<IDisposable> _held = [];
    internal NotificationCatalogSnapshot Catalog { get; private set; } = null!;
    internal ImmutableArray<NotificationWorkTarget> Targets { get; private set; }

    internal static PreparedNotificationTargets Capture(RuntimeInstance instance, StateDatabase database, ContractReader contracts, IReadOnlyList<Guid> ids)
    {
        var result = new PreparedNotificationTargets();
        try
        {
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                var cursor = NotificationCatalogRows.ReadCurrent(connection, transaction);
                result.Catalog = cursor is null ? new NotificationCatalogCodec(instance, contracts).Persist(NotificationTaskCatalog.Empty) :
                    new NotificationCatalogCodec(instance, contracts).Read(cursor.Reference);
                NotificationCatalogRows.RequireTaskEntries(connection, transaction, result.Catalog.Document.Tasks);
                var targets = ImmutableArray.CreateBuilder<NotificationWorkTarget>();
                foreach (var id in ids)
                {
                    var task = result.Catalog.Catalog.Tasks.SingleOrDefault(item => item.Id == id);
                    var entry = result.Catalog.Document.Tasks.SingleOrDefault(item => item.TaskId == id);
                    var fields = ImmutableArray.CreateBuilder<NotificationDeliverySecret>();
                    if (task?.IsEnabled == true)
                        foreach (var field in Enum.GetValues<NotificationSecretField>())
                        {
                            var reference = SecretFieldRows.ReadField(connection, transaction, SecretFieldKey.NotificationTask(instance.InstanceId, id, field));
                            fields.Add(new(field, reference, reference.SecretId is { } secret ? SecretFieldStore.ReadPayload(connection, transaction, secret) : null));
                        }
                    targets.Add(new(id, task is null ? "missing" : task.IsEnabled ? "queued" : "disabled", entry, fields.ToImmutable()));
                }
                result.Targets = targets.ToImmutable(); transaction.Commit();
            }
            var blobs = new ImmutableBlobStore(instance, contracts);
            result._held.Add(blobs.OpenVerified(result.Catalog.Reference));
            foreach (var entry in result.Catalog.Document.Tasks) result._held.Add(blobs.OpenVerified(entry.Definition));
            var files = new SecretPayloadFiles(instance, new UserSecretProtector());
            foreach (var payload in result.Targets.SelectMany(item => item.Secrets).Select(item => item.Payload).OfType<SecretPayloadIdentity>().Distinct())
                result._held.Add(files.HoldVerified(payload));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal void RequireCurrent(SqliteConnection connection, SqliteTransaction transaction)
    {
        var cursor = NotificationCatalogRows.ReadCurrent(connection, transaction);
        if (Catalog.Catalog.Revision == 0 ? cursor is not null : cursor is null || cursor.Revision != Catalog.Catalog.Revision || cursor.Reference != Catalog.Reference)
            throw new NotificationTaskException("notification_revision_conflict");
        NotificationCatalogRows.RequireTaskEntries(connection, transaction, Catalog.Document.Tasks);
        foreach (var target in Targets)
            foreach (var field in target.Secrets)
            {
                SecretFieldRows.RequireReference(connection, transaction, SecretFieldKey.NotificationTask(Catalog.Document.InstanceId, target.TaskId, field.Field), field.Reference);
                NotificationDeliveryRows.RequirePayload(connection, transaction, field, allowRetired: false);
            }
    }

    public void Dispose() { foreach (var held in _held) held.Dispose(); _held.Clear(); }
}
