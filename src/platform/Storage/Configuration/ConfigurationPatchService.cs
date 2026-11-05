using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationPatchService
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ConfigurationRevisionStore _store;
    private readonly ImmutableBlobStore _blobs;
    private readonly ConfigurationPatchReader _reader;

    public ConfigurationPatchService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources)
    {
        lease.VerifyHeld(instance);
        _instance = instance; _lease = lease; _resources = resources;
        _store = new(instance, database, contracts); _blobs = new(instance, contracts); _reader = new(contracts);
    }

    public PreparedConfigurationPatch Preview(string subject, ConfigurationAddress address, long expectedBindingRevision,
        ConfigurationContractStamp contract, string relativePath, ConfigurationSyntax syntax, ConfigurationDocumentAccess access,
        ReadOnlyMemory<byte> patchBytes)
        => PreviewCore(subject, address, expectedBindingRevision, contract, relativePath, syntax, ConfigurationPatchPolicy.Freeze(access), _reader.Read(patchBytes), null);

    internal PreparedConfigurationPatch PreviewAdapter(string subject, ConfigurationAddress address, long expectedBindingRevision,
        ConfigurationContractStamp contract, string relativePath, ConfigurationSyntax syntax, ConfigurationDocumentAccess access,
        ReadOnlyMemory<byte> patchBytes, string sourceRequestDigest)
        => PreviewCore(subject, address, expectedBindingRevision, contract, relativePath, syntax, ConfigurationPatchPolicy.Freeze(access), _reader.Read(patchBytes), sourceRequestDigest);

    internal PreparedConfigurationPatch PreviewCore(string subject, ConfigurationAddress address, long expectedBindingRevision,
        ConfigurationContractStamp contract, string relativePath, ConfigurationSyntax syntax, ConfigurationDocumentAccess access,
        ConfigurationPatchRequest request, string? sourceRequestDigest)
    {
        _lease.VerifyHeld(_instance);
        var before = _store.Read(address);
        ConfigurationRevisionPolicy.RequireCurrentContract(before, contract);
        if (before!.Revision != request.Binding.Revision || request.Binding.ResourceId != address.ResourceId)
            throw new ConfigurationException("configuration_patch_binding_conflict");
        var entry = before.Tree.Entries.SingleOrDefault(entry => entry.RelativePath == relativePath && entry.Kind == FileObjectKind.File)
            ?? throw new ConfigurationException("configuration_resource_missing");
        if (entry.SizeBytes > StrictJson.MaximumBytes || entry.Sha256 != request.Binding.SourceSha256)
            throw new ConfigurationException("configuration_source_changed");
        var source = _blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
        byte[]? candidate = null;
        try
        {
            var bytes = new byte[checked((int)entry.SizeBytes)];
            try
            {
                source.ReadExactly(bytes);
                using var document = new ConfigurationDocument(request.Binding, syntax, access, bytes);
                candidate = document.Apply(request);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            var digest = Convert.ToHexStringLower(SHA256.HashData(candidate));
            var tree = before.Tree with { Entries = Array.AsReadOnly(before.Tree.Entries.Select(item => item == entry
                ? item with { Sha256 = digest, SizeBytes = candidate.Length } : item).ToArray()) };
            var change = ConfigurationRevisionPolicy.Freeze(new ConfigurationRevisionChange(address, before.Revision, expectedBindingRevision,
                contract, tree, ConfigurationUpdateKind.Repair)
                { SourceRequestDigest = sourceRequestDigest ?? SourceDigest(address, expectedBindingRevision, contract, relativePath, syntax, access, request) });
            var operation = ConfigurationRevisionStore.Identity(request.Binding.OperationId, subject, change);
            return new(_instance, change, operation, candidate, source, digest != entry.Sha256);
        }
        catch
        {
            source.Dispose(); if (candidate is not null) CryptographicOperations.ZeroMemory(candidate);
            throw;
        }
    }

    public ConfigurationSaveResult Apply(string subject, ConfigurationAddress address, long expectedBindingRevision,
        ConfigurationContractStamp contract, string relativePath, ConfigurationSyntax syntax, ConfigurationDocumentAccess access,
        ReadOnlyMemory<byte> patchBytes, DateTimeOffset acceptedAt, CancellationToken cancellationToken = default)
    {
        _lease.VerifyHeld(_instance);
        var request = _reader.Read(patchBytes);
        access = ConfigurationPatchPolicy.Freeze(access);
        if (request.Binding.ResourceId != address.ResourceId) throw new ConfigurationException("configuration_patch_binding_conflict");
        var digest = SourceDigest(address, expectedBindingRevision, contract, relativePath, syntax, access, request);
        var replay = _store.LookupSource(request.Binding.OperationId, subject, address, digest);
        if (replay is not null) return replay;
        using var preview = PreviewCore(subject, address, expectedBindingRevision, contract, relativePath, syntax, access, request, digest);
        return Commit(preview, acceptedAt, cancellationToken);
    }

    internal ConfigurationSaveResult? ReadAccepted(string subject, ConfigurationAddress address,
        ConfigurationPatchRequest request, string sourceDigest)
    {
        _lease.VerifyHeld(_instance);
        if (request.Binding.ResourceId != address.ResourceId) throw new ConfigurationException("configuration_patch_binding_conflict");
        return _store.LookupSource(request.Binding.OperationId, subject, address, sourceDigest);
    }

    internal static string SourceDigest(ConfigurationAddress address, long expectedBindingRevision, ConfigurationContractStamp contract,
        string relativePath, ConfigurationSyntax syntax, ConfigurationDocumentAccess access, ConfigurationPatchRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new { address, expectedBindingRevision, contract, relativePath, syntax, access, request })));

    public ConfigurationSaveResult Commit(PreparedConfigurationPatch preview, DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
        preview.Use((change, operation, candidate) =>
        {
            _lease.VerifyHeld(_instance);
            if (preview.InstanceId != _instance.InstanceId || preview.RuntimeRoot != _instance.RuntimeRoot)
                throw new IOException("configuration_patch_instance_unbound");
            var replay = _store.Lookup(operation, change);
            if (replay is not null) return replay;
            var admission = _resources.TryAcquire(operation.OperationId, ConfigurationRevisionService.Claims(change.Address));
            using var resources = admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
                ? "configuration_resource_conflict" : "configuration_host_unavailable");
            _blobs.PersistContent(candidate, cancellationToken);
            return _store.Commit(operation, change, acceptedAt);
        });
    internal ConfigurationSaveResult CommitOwned(PreparedConfigurationPatch preview, ResourceOperationLease actualRoot,
        Action requireCurrent, DateTimeOffset acceptedAt, CancellationToken cancellationToken) => preview.Use((change, operation, candidate) =>
        {
            _lease.VerifyHeld(_instance);
            if (preview.InstanceId != _instance.InstanceId || preview.RuntimeRoot != _instance.RuntimeRoot) throw new IOException("configuration_patch_instance_unbound");
            using var held = actualRoot.Borrow(_resources, operation.OperationId, ConfigurationRevisionService.Claims(change.Address));
            requireCurrent();
            var replay = _store.Lookup(operation, change); if (replay is not null) return replay;
            _blobs.PersistContent(candidate, cancellationToken); requireCurrent();
            return _store.Commit(operation, change, acceptedAt);
        });

}
