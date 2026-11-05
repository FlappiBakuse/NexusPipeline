using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationCatalogCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal NotificationCatalogSnapshot Persist(NotificationTaskCatalog catalog)
    {
        NotificationTaskPolicy.Validate(catalog);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var entries = catalog.Tasks.Select(task => new NotificationCatalogEntry(task.Id, task.Revision,
            blobs.PersistPlan(Encode(new NotificationTaskDocument("nexus.notification.task", 1, instance.InstanceId, task)))))
            .ToImmutableArray();
        var document = new NotificationCatalogDocument("nexus.notification.catalog", 1, instance.InstanceId,
            catalog.Revision, catalog.MaximumTasks, entries);
        var reference = blobs.PersistPlan(Encode(document));
        return Read(reference);
    }

    internal NotificationCatalogSnapshot Read(BlobReference reference)
    {
        var document = ReadDocument<NotificationCatalogDocument>(reference);
        if (document.Format != "nexus.notification.catalog" || document.Version != 1 || document.InstanceId != instance.InstanceId ||
            document.Tasks.IsDefault || document.Tasks.Length > 99 || document.Tasks.Select(item => item.TaskId).Distinct().Count() != document.Tasks.Length)
            throw new IOException("notification_catalog_document_invalid");
        var tasks = document.Tasks.Select(entry =>
        {
            var saved = ReadDocument<NotificationTaskDocument>(entry.Definition);
            if (saved.Format != "nexus.notification.task" || saved.Version != 1 || saved.InstanceId != instance.InstanceId ||
                saved.Task is null || saved.Task.Id != entry.TaskId || saved.Task.Revision != entry.Revision)
                throw new IOException("notification_task_reference_unbound");
            return saved.Task;
        }).ToImmutableArray();
        var catalog = new NotificationTaskCatalog(document.Revision, document.MaximumTasks, tasks);
        NotificationTaskPolicy.Validate(catalog);
        return new(catalog, document, reference);
    }

    internal void RequirePublishedCursor(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction, NotificationCatalogSnapshot catalog)
    {
        if (catalog.Catalog.Revision == 0)
        {
            if (catalog.Catalog.MaximumTasks != 10 || catalog.Catalog.Tasks.Length != 0)
                throw new IOException("notification_catalog_initial_invalid");
            return;
        }
        var publication = new NotificationPublicationCodec(instance, contracts).Read(
            NotificationCatalogRows.ReadPublishedReference(connection, transaction, catalog.Catalog.Revision));
        if (publication.After.Reference != catalog.Reference || publication.After.Catalog.Revision != catalog.Catalog.Revision)
            throw new IOException("notification_delivery_catalog_unbound");
    }

    internal T ReadDocument<T>(BlobReference reference)
    {
        RequirePlan(reference);
        using var input = new ImmutableBlobStore(instance, contracts).OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return Decode<T>(bytes);
    }

    internal static T Decode<T>(ReadOnlyMemory<byte> bytes)
    {
        using var parsed = StrictJson.Parse(bytes);
        RequireRequiredMembers(parsed.RootElement, typeof(T));
        return JsonSerializer.Deserialize<T>(bytes.Span, Options) ?? throw new JsonException("notification_document_missing");
    }

    internal static byte[] Encode<T>(T document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        using var parsed = StrictJson.Parse(bytes);
        return bytes;
    }

    internal static void RequirePlan(BlobReference reference)
    {
        if (reference is null || reference.Sha256 is null || reference.Sha256.Length != 64 ||
            reference.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            reference.SizeBytes is < 1 or > StrictJson.MaximumBytes || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json")
            throw new IOException("notification_plan_reference_invalid");
    }

    private static void RequireRequiredMembers(JsonElement element, Type type)
    {
        // Absent false/zero members must not acquire authority in persisted documents.
        if (element.ValueKind == JsonValueKind.Null) return;
        if (type == typeof(string) || type == typeof(Guid) || type.IsPrimitive || type.IsEnum || type == typeof(decimal)) return;
        if (Nullable.GetUnderlyingType(type) is { } underlying) { RequireRequiredMembers(element, underlying); return; }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            foreach (var item in element.EnumerateArray()) RequireRequiredMembers(item, type.GenericTypeArguments[0]);
            return;
        }
        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (!element.TryGetProperty(name, out var value)) throw new JsonException("notification_required_member_missing");
            RequireRequiredMembers(value, property.PropertyType);
        }
    }
}
