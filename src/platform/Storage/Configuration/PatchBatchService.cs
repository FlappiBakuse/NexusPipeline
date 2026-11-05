using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class PatchBatchService
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ConfigurationRevisionStore _revisions;
    private readonly ConfigurationPatchService _patches;
    private readonly ConfigurationBatchStore _batches;
    private readonly ImmutableBlobStore _blobs;
    private readonly ConfigurationPatchReader _reader;

    public PatchBatchService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, IBatchPersistenceObserver? observer = null)
    {
        lease.VerifyHeld(instance); _instance = instance; _lease = lease; _resources = resources;
        _revisions = new(instance, database, contracts); _patches = new(instance, database, contracts, lease, resources);
        _batches = new(instance, database, contracts, observer); _blobs = new(instance, contracts); _reader = new(contracts);
    }

    public PreparedPatchBatch Preview(string subject, IReadOnlyList<PatchBatchItem> items,
        IReadOnlyList<ConfigurationReadStamp>? readSet = null)
    {
        _lease.VerifyHeld(_instance);
        using var request = PatchBatchRequest.Freeze(items, _reader, readSet);
        return Preview(subject, request);
    }

    public ConfigurationBatchResult Apply(string subject, IReadOnlyList<PatchBatchItem> items, DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default, IReadOnlyList<ConfigurationReadStamp>? readSet = null)
    {
        _lease.VerifyHeld(_instance);
        using var request = PatchBatchRequest.Freeze(items, _reader, readSet);
        var replay = _batches.LookupSource(request.OperationId, subject, request.Sources);
        if (replay is not null) return replay;
        using var prepared = Preview(subject, request);
        return Commit(prepared, acceptedAt, cancellationToken);
    }

    private PreparedPatchBatch Preview(string subject, PatchBatchRequest request)
    {
        _ = new OperationIdentity(request.OperationId, subject, new('0', 64), "configuration.patch-batch");
        var documents = new List<PreparedConfigurationPatch>();
        try
        {
            var changes = ImmutableArray.CreateBuilder<ConfigurationRevisionChange>();
            foreach (var group in request.Documents.GroupBy(document => document.Input.Address))
            {
                var before = _revisions.Read(group.Key) ?? throw new ConfigurationException("configuration_required");
                ConfigurationRevisionPolicy.RequireCurrentContract(before, group.First().Input.Contract);
                var replacements = new Dictionary<string, FileTreeEntry>(StringComparer.Ordinal);
                foreach (var document in group)
                {
                    var item = document.Input;
                    if (document.Patch.Binding.Revision != before.Revision)
                        throw new ConfigurationException("configuration_patch_binding_conflict");
                    var preview = _patches.Preview(subject, item.Address, item.ExpectedBindingRevision, item.Contract,
                        item.RelativePath, item.Syntax, item.Access, item.PatchBytes);
                    documents.Add(preview);
                    var replacement = preview.Use((change, _, _) =>
                    {
                        if (change.ExpectedRevision != before.Revision || change.Contract != before.Contract ||
                            change.Tree.Shape != before.Tree.Shape || !change.Tree.Entries.Where(entry => entry.RelativePath != item.RelativePath)
                                .SequenceEqual(before.Tree.Entries.Where(entry => entry.RelativePath != item.RelativePath)))
                            throw new ConfigurationException("configuration_patch_batch_source_invalid");
                        return change.Tree.Entries.Single(entry => entry.RelativePath == item.RelativePath);
                    });
                    replacements.Add(item.RelativePath, replacement);
                }
                var first = group.First().Input;
                var tree = before.Tree with { Entries = Array.AsReadOnly(before.Tree.Entries.Select(entry =>
                    replacements.GetValueOrDefault(entry.RelativePath) ?? entry).ToArray()) };
                changes.Add(ConfigurationRevisionPolicy.Freeze(new(first.Address, before.Revision, first.ExpectedBindingRevision,
                    first.Contract, tree, ConfigurationUpdateKind.Repair) { SourceRequestDigest = request.Sources.Single(source => source.Address == group.Key).Digest }));
            }
            var batch = RevisionBatchPolicy.Freeze(new(changes.ToImmutable()));
            return new(_instance, batch, ConfigurationBatchStore.Identity(request.OperationId, subject, batch), documents, request.ReadSet);
        }
        catch { for (var index = documents.Count - 1; index >= 0; index--) documents[index].Dispose(); throw; }
    }

    public ConfigurationBatchResult Commit(PreparedPatchBatch prepared, DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default) => prepared.Use((batch, operation, documents) =>
    {
        _lease.VerifyHeld(_instance);
        if (prepared.InstanceId != _instance.InstanceId || prepared.RuntimeRoot != _instance.RuntimeRoot)
            throw new IOException("configuration_patch_instance_unbound");
        var replay = _batches.Lookup(operation, batch);
        if (replay is not null) return replay;
        var claims = batch.Changes.SelectMany(change => ConfigurationRevisionService.Claims(change.Address))
            .Concat(prepared.ReadSet.Where(item => !batch.Changes.Any(change => change.Address == item.Address))
                .Select(item => new ResourceClaim(ConfigurationRevisionService.ResourceKey(item.Address), ResourceClaimMode.Reference)))
            .Distinct().ToArray();
        var admission = _resources.TryAcquire(operation.OperationId, claims);
        using var lease = admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "configuration_resource_conflict" : "configuration_host_unavailable");
        using var readLease = new ConfigurationReadLease(_instance, _revisions, _blobs, prepared.ReadSet);
        foreach (var document in documents)
            document.Use((_, _, candidate) => _blobs.PersistContent(candidate, cancellationToken));
        using var frozen = _batches.Prepare(batch);
        return _batches.CommitChecked(operation, frozen, prepared.ReadSet, acceptedAt);
    });
}
