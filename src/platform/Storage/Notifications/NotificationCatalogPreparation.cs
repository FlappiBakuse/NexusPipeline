using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

public sealed record NotificationSecretEdit(NotificationSecretField Field, long ExpectedRevision,
    SecretDisposition Disposition, SecretValue? Value);

internal sealed class PreparedNotificationPublication(NotificationPublicationSnapshot publication,
    ImmutableArray<PreparedSecretPayload> payloads, List<IDisposable> held) : IDisposable
{
    internal NotificationPublicationSnapshot Publication { get; } = publication;
    internal ImmutableArray<PreparedSecretPayload> Payloads { get; } = payloads;
    public void Dispose()
    {
        foreach (var payload in Payloads) payload.Dispose();
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
        held.Clear();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class NotificationCatalogPreparation(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal PreparedNotificationPublication Freeze(ResourceOperationLease root, ResourceLeaseManager resources,
        Guid operationId, string subject, NotificationCatalogRequest request, IReadOnlyList<NotificationSecretEdit> edits)
    {
        RequireEdits(request, edits);
        var codec = new NotificationCatalogCodec(instance, contracts);
        NotificationCatalogCursor? cursor;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System))
            cursor = NotificationCatalogRows.ReadCurrent(connection, null);
        var before = cursor is null ? codec.Persist(NotificationTaskCatalog.Empty) : codec.Read(cursor.Reference);
        if (cursor is not null && before.Catalog.Revision != cursor.Revision) throw new IOException("notification_catalog_cursor_unbound");
        var after = codec.Persist(NotificationPublicationCodec.Apply(before.Catalog, request));
        var original = ReadSecrets(request, edits, operationId);
        var identity = new OperationIdentity(operationId, subject,
            NotificationPublicationCodec.Digest(instance.InstanceId, subject, request, original), NotificationPublicationCodec.Kind(request.Change));
        var held = new List<IDisposable>();
        var payloads = ImmutableArray.CreateBuilder<PreparedSecretPayload>();
        try
        {
            var changes = ImmutableArray.CreateBuilder<NotificationSecretChange>();
            using var batch = request.Change == NotificationCatalogChange.Save ?
                PreparedSecretBatch.Acquire(instance, root, resources, identity, request.TaskId!.Value, original) : null;
            foreach (var item in original)
            {
                PreparedSecretPayload? payload = null;
                SecretFieldRetirement? retirement = null;
                if (item.Disposition == SecretDisposition.Set)
                {
                    payload = new PreparationPlanCodec(instance, contracts, database).FreezeNotification(batch!, item.Field,
                        edits.Single(edit => edit.Field == item.Field).Value!);
                    payloads.Add(payload);
                    retirement = payload.Retirement;
                }
                else if (item.Disposition == SecretDisposition.Clear && item.Before.SecretId is { } oldId)
                {
                    retirement = SecretFieldRetirement.Prepare(instance, database,
                        NotificationPublicationCodec.ChildOperation(operationId, item.Field), oldId);
                    held.Add(retirement);
                }
                var plan = payload?.Plan.ReadWire();
                var next = SecretMutationPolicy.Apply(item.Before, item.Before.Revision, item.Disposition,
                    plan is null ? null : Guid.Parse(plan.Candidate.SecretId));
                var readable = item.Disposition == SecretDisposition.Set ||
                    item.Disposition == SecretDisposition.Keep && ReadStatus(request.TaskId!.Value, item.Field).IsReadable;
                changes.Add(new(item.Field, NotificationPublicationCodec.ChildOperation(operationId, item.Field), item.Disposition,
                    item.Before, next, readable, item.ReplayProof, plan is null ? null : Guid.Parse(plan.JournalId),
                    payload?.Reference, retirement?.DescribeNotification()));
            }
            var document = new NotificationPublicationDocument("nexus.notification.publication", 1, instance.InstanceId,
                operationId, subject, identity.RequestDigest, request, before.Reference, after.Reference, changes.ToImmutable());
            var publication = new NotificationPublicationCodec(instance, contracts).Persist(document);
            var blobs = new ImmutableBlobStore(instance, contracts);
            held.Add(blobs.OpenVerified(publication.Reference));
            foreach (var reference in new[] { before.Reference, after.Reference }.Concat(
                         before.Document.Tasks.Concat(after.Document.Tasks).Select(task => task.Definition)).Distinct())
                held.Add(blobs.OpenVerified(reference));
            return new(publication, payloads.ToImmutable(), held);
        }
        catch
        {
            foreach (var payload in payloads) payload.Dispose();
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            throw;
        }
    }

    internal static void RequireEdits(NotificationCatalogRequest request, IReadOnlyList<NotificationSecretEdit> edits)
    {
        if (edits is null || edits.Count > 5 || edits.Select(edit => edit.Field).Distinct().Count() != edits.Count ||
            request.Change != NotificationCatalogChange.Save && edits.Count != 0)
            throw new ArgumentException("notification_secret_edits_invalid");
        foreach (var edit in edits)
            if (!Enum.IsDefined(edit.Field) || !Enum.IsDefined(edit.Disposition) || edit.ExpectedRevision is < 0 or > SecretFieldReference.MaximumRevision ||
                (edit.Disposition == SecretDisposition.Set) != (edit.Value is not null))
                throw new ArgumentException("notification_secret_edit_invalid");
    }

    internal ImmutableArray<NotificationSecretRequest> ReadSecrets(NotificationCatalogRequest request, IReadOnlyList<NotificationSecretEdit> edits,
        Guid operationId, NotificationPublicationSnapshot? replay = null)
    {
        if (request.Change is not (NotificationCatalogChange.Save or NotificationCatalogChange.Delete)) return [];
        return Enum.GetValues<NotificationSecretField>().Select(field =>
        {
            var edit = edits.SingleOrDefault(item => item.Field == field);
            var previous = replay?.Document.Secrets.Single(item => item.Field == field).Before;
            var status = previous is null ? ReadStatus(request.TaskId!.Value, field) : null;
            var before = previous ?? new SecretFieldReference(status!.Revision, status.SecretId);
            if (edit is not null && edit.ExpectedRevision != before.Revision) throw new InvalidOperationException("secret_revision_conflict");
            var disposition = request.Change == NotificationCatalogChange.Delete ? SecretDisposition.Clear : edit?.Disposition ?? SecretDisposition.Keep;
            string? proof = null;
            if (disposition == SecretDisposition.Set)
            {
                var key = SecretFieldKey.NotificationTask(instance.InstanceId, request.TaskId!.Value, field);
                var child = SecretFieldStore.Identity(NotificationPublicationCodec.ChildOperation(operationId, field), key, before.Revision, disposition);
                using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
                proof = SecretReplayProof.Compute(instance, connection, child, edit!.Value!);
            }
            return new NotificationSecretRequest(field, disposition, before, proof);
        }).ToImmutableArray();
    }

    private SecretFieldStatus ReadStatus(Guid taskId, NotificationSecretField field) =>
        new SecretFieldStore(instance, database).GetStateAsync(SecretFieldKey.NotificationTask(instance.InstanceId, taskId, field), default)
            .GetAwaiter().GetResult();
}
