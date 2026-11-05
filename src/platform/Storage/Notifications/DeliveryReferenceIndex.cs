using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class DeliveryReferenceIndex
{
    internal static bool IsBlobReferenced(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        BlobReference candidate) => ReadReferenced(instance, database, contracts, new HashSet<BlobReference> { candidate }).Contains(candidate);

    internal static HashSet<BlobReference> ReadReferenced(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        IReadOnlySet<BlobReference> candidates)
    {
        var referenced = new HashSet<BlobReference>();
        var secrets = candidates.Where(item => CleanupCandidateFiles.SecretId(item) is not null)
            .GroupBy(item => CleanupCandidateFiles.SecretId(item)!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        var hasContent = secrets.Sum(item => item.Value.Length) != candidates.Count;
        void Include(IEnumerable<BlobReference> values) => referenced.UnionWith(values.Where(candidates.Contains));
        void IncludeSecret(Guid id, string digest, long sizeBytes)
        {
            if (secrets.TryGetValue(id, out var matches))
                referenced.UnionWith(matches.Where(item => item.Sha256 == digest && item.SizeBytes == sizeBytes));
        }
        NotificationReferenceInventory.Visit(instance, database, contracts, (source, expanded, routes) =>
        {
            var document = source.Snapshot.Document;
            if (!expanded)
                foreach (var item in document.Tasks.SelectMany(task => task.Secrets))
                    if (item.Payload is { } payload) IncludeSecret(payload.SecretId, payload.Digest, payload.SizeBytes);
            if (!hasContent) return;
            Include(routes);
            Include(new[] { source.Snapshot.Reference, document.TotalPlan, document.DefinitionBundle, document.Catalog }
                .Concat(source.Catalog.Document.Tasks.Select(task => task.Definition)));
            if (!expanded && document.Screenshot is { } screenshot)
                Include([new($"blobs/content/{screenshot.Sha256}.bin", screenshot.SizeBytes, screenshot.Sha256)]);
        }, (delivery, state) =>
        {
            if (!NotificationDeliveryStates.ReleasesReferences(state.State))
            {
                foreach (var item in delivery.Document.Secrets)
                    if (item.Payload is { } payload) IncludeSecret(payload.SecretId, payload.Digest, payload.SizeBytes);
                if (state.Network?.ProxyPayload is { } proxy) IncludeSecret(proxy.SecretId, proxy.Digest, proxy.SizeBytes);
            }
            if (hasContent) Include(new NotificationDeliveryEvidence(instance, database, contracts).ReadReferences(delivery, state.State));
        }, source =>
        {
            if (!hasContent) return;
            Include([source.Reference, source.Document.Catalog, source.Document.Task.Definition]);
        }, source =>
        {
            if (!hasContent) return;
            Include(new[] { source.Reference, source.Document.Invocation, source.Document.Begin, source.Document.Catalog }
                .Concat(new NotificationCatalogCodec(instance, contracts).Read(source.Document.Catalog).Document.Tasks.Select(item => item.Definition)));
        }, source =>
        {
            if (!hasContent) return;
            Include(new[] { source.Reference, source.Document.Invocation, source.Document.Begin, source.Document.Catalog }
                .Concat(new NotificationCatalogCodec(instance, contracts).Read(source.Document.Catalog).Document.Tasks.Select(item => item.Definition)));
        });
        return referenced;
    }

    internal static bool IsSecretReferenced(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, Guid secretId, string digest, long sizeBytes)
    {
        var candidate = new BlobReference($"secrets/payloads/{secretId:D}-{digest}.bin", sizeBytes, digest);
        return ReadReferenced(instance, database, contracts, new HashSet<BlobReference> { candidate }).Contains(candidate);
    }

    internal static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts) =>
        NotificationReferenceInventory.Visit(instance, database, contracts, (_, _, _) => { }, (_, _) => { });
}
