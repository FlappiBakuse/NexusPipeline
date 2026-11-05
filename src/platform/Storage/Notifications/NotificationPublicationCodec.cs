using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationPublicationCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal static string Kind(NotificationCatalogChange change) => change switch
    {
        NotificationCatalogChange.Save => "notification.task.save",
        NotificationCatalogChange.Delete => "notification.task.delete",
        NotificationCatalogChange.Reorder => "notification.catalog.reorder",
        NotificationCatalogChange.Limit => "notification.catalog.limit",
        _ => throw new IOException("notification_change_invalid")
    };

    internal static Guid ChildOperation(Guid operationId, NotificationSecretField field) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"notification-secret/{operationId:D}/{NotificationSecretFields.Name(field)}")).AsSpan(0, 16));

    internal static string Digest(Guid instanceId, string subject, NotificationCatalogRequest request,
        ImmutableArray<NotificationSecretChange> secrets) =>
        Digest(instanceId, subject, request, secrets.Select(item =>
            new NotificationSecretRequest(item.Field, item.Disposition, item.Before, item.ReplayProof)).ToImmutableArray());

    internal static string Digest(Guid instanceId, string subject, NotificationCatalogRequest request,
        ImmutableArray<NotificationSecretRequest> secrets) =>
        Convert.ToHexStringLower(SHA256.HashData(NotificationCatalogCodec.Encode(new { instanceId, subject, request, secrets })));

    internal static OperationIdentity Identity(NotificationPublicationDocument document) =>
        new(document.OperationId, document.SubjectId, document.RequestDigest, Kind(document.Request.Change));

    internal NotificationPublicationSnapshot Persist(NotificationPublicationDocument document)
    {
        var reference = new ImmutableBlobStore(instance, contracts).PersistPlan(NotificationCatalogCodec.Encode(document));
        return Read(reference);
    }

    internal NotificationPublicationSnapshot Read(BlobReference reference)
    {
        var codec = new NotificationCatalogCodec(instance, contracts);
        var document = codec.ReadDocument<NotificationPublicationDocument>(reference);
        if (document.Format != "nexus.notification.publication" || document.Version != 1 ||
            document.InstanceId != instance.InstanceId || document.OperationId == Guid.Empty || document.Request is null ||
            document.Secrets.IsDefault || document.Secrets.Length > 5 || document.Secrets.Any(item => item is null))
            throw new IOException("notification_publication_invalid");
        _ = Identity(document);
        if (Digest(document.InstanceId, document.SubjectId, document.Request, document.Secrets) != document.RequestDigest)
            throw new IOException("notification_publication_request_unbound");
        var before = codec.Read(document.Before); var after = codec.Read(document.After);
        var expected = Apply(before.Catalog, document.Request);
        if (!NotificationCatalogCodec.Encode(expected).AsSpan().SequenceEqual(NotificationCatalogCodec.Encode(after.Catalog)))
            throw new IOException("notification_publication_catalog_unbound");
        RequireSecrets(document);
        return new(document, reference, before, after);
    }

    internal static NotificationTaskCatalog Apply(NotificationTaskCatalog before, NotificationCatalogRequest request)
    {
        if (request is null || request.Order.IsDefault) throw new IOException("notification_request_invalid");
        return request.Change switch
        {
            NotificationCatalogChange.Save when request.TaskId is { } id && request.ExpectedTaskRevision is { } revision &&
                request.Input is not null && request.Order.Length == 0 && request.MaximumTasks is null =>
                NotificationTaskPolicy.Save(before, request.ExpectedCatalogRevision, id, revision, request.Input),
            NotificationCatalogChange.Delete when request.TaskId is { } id && request.ExpectedTaskRevision is { } revision &&
                request.Input is null && request.Order.Length == 0 && request.MaximumTasks is null =>
                NotificationTaskPolicy.Remove(before, request.ExpectedCatalogRevision, id, revision),
            NotificationCatalogChange.Reorder when request.TaskId is null && request.ExpectedTaskRevision is null &&
                request.Input is null && request.MaximumTasks is null =>
                NotificationTaskPolicy.Reorder(before, request.ExpectedCatalogRevision, request.Order),
            NotificationCatalogChange.Limit when request.TaskId is null && request.ExpectedTaskRevision is null &&
                request.Input is null && request.Order.Length == 0 && request.MaximumTasks is { } maximum =>
                NotificationTaskPolicy.SetLimit(before, request.ExpectedCatalogRevision, maximum),
            _ => throw new IOException("notification_request_invalid")
        };
    }

    private static void RequireSecrets(NotificationPublicationDocument document)
    {
        var mutations = document.Request.Change is NotificationCatalogChange.Save or NotificationCatalogChange.Delete;
        var fields = Enum.GetValues<NotificationSecretField>();
        if ((!mutations && document.Secrets.Length != 0) || (mutations &&
            !document.Secrets.Select(value => value.Field).SequenceEqual(fields)) ||
            document.Secrets.Select(value => value.OperationId).Distinct().Count() != document.Secrets.Length)
            throw new IOException("notification_secret_set_invalid");
        foreach (var change in document.Secrets)
        {
            if (change is null || change.Before is null || change.After is null) throw new IOException("notification_secret_result_unbound");
            var retire = change.Disposition != SecretDisposition.Keep && change.Before.SecretId is not null;
            if (retire != (change.Retirement is not null)) throw new IOException("notification_retirement_missing");
            if (change.Retirement is { } retirement)
            {
                if (retirement.State is not ("planned" or "unverified") ||
                    (retirement.State == "planned") != (retirement.JournalId is not null && retirement.Plan is not null) ||
                    retirement.JournalId == Guid.Empty ||
                    retirement.State == "unverified" && (retirement.JournalId is not null || retirement.Plan is not null))
                    throw new IOException("notification_retirement_invalid");
                if (retirement.Plan is not null) NotificationCatalogCodec.RequirePlan(retirement.Plan);
            }
            if (change.OperationId != ChildOperation(document.OperationId, change.Field) || change.OperationId == document.OperationId || change.Before is null || change.After is null ||
                SecretMutationPolicy.Apply(change.Before, change.Before.Revision, change.Disposition,
                    change.Disposition == SecretDisposition.Set ? change.After.SecretId : null) != change.After)
                throw new IOException("notification_secret_result_unbound");
            if (document.Request.Change == NotificationCatalogChange.Delete && change.Disposition != SecretDisposition.Clear)
                throw new IOException("notification_delete_must_clear_credentials");
            if (change.Disposition == SecretDisposition.Set)
            {
                if (change.Preparation is null || change.JournalId is null || change.JournalId == Guid.Empty ||
                    change.After.SecretId is null || !change.ResultReadable || !FileEffectRows.IsDigest(change.ReplayProof ?? ""))
                    throw new IOException("notification_secret_preparation_missing");
                NotificationCatalogCodec.RequirePlan(change.Preparation);
            }
            else if (change.Preparation is not null || change.JournalId is not null || change.ReplayProof is not null ||
                (change.Disposition == SecretDisposition.Clear && change.ResultReadable))
                throw new IOException("notification_secret_preparation_unexpected");
        }
    }
}
