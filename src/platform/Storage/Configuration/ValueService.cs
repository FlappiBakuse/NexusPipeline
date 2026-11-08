using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record ValuePreview(Guid OperationId, int SizeBytes, bool HasChanges);
public sealed record GenericConfigurationFile(int Position, string RelativePath, long SizeBytes, string Sha256);
public sealed record GenericConfigurationFiles(IReadOnlyList<GenericConfigurationFile> Items, int? NextPosition);
public sealed record GenericConfigurationResource(string ResourceId, long BindingRevision, long Revision,
    bool IsSaved, FileObjectKind Shape, int FileCount);
public sealed record GenericConfigurationDocument(long Revision, string SourceSha256, JsonElement Value);

public sealed class ValueService
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly AutomationDefinitionStore _definitions;
    private readonly ConfigurationRevisionStore _revisions;
    private readonly ConfigurationPatchService _patches;
    private readonly ConfigurationPatchReader _reader;
    private readonly ResourceLeaseManager _resources;
    private readonly ImmutableBlobStore _blobs;

    public ValueService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources)
    {
        lease.VerifyHeld(instance);
        _instance = instance; _lease = lease; _resources = resources; _definitions = new(database);
        _revisions = new(instance, database, contracts); _blobs = new(instance, contracts);
        _patches = new(instance, database, contracts, lease, resources); _reader = new(contracts);
    }

    public IReadOnlyList<GenericConfigurationResource> Read(Guid bindingId)
    {
        var (binding, script) = Definitions(bindingId);
        return Array.AsReadOnly(script.ConfigurationPaths.Select(path =>
        {
            var saved = _revisions.Read(new(bindingId, GenericConfigurationPolicy.ResourceId(path)));
            if (saved is not null) saved = GenericConfigurationPolicy.RequireSaved(binding, script, path, _revisions);
            return new GenericConfigurationResource(GenericConfigurationPolicy.ResourceId(path), binding.Revision, saved?.Revision ?? 0,
                saved is not null, saved?.Tree.Shape ?? FileObjectKind.Missing,
                saved?.Tree.Entries.Count(item => item.Kind == FileObjectKind.File) ?? 0);
        }).ToArray());
    }

    public GenericConfigurationFiles ReadFiles(Guid bindingId, string resourceId, long revision, int afterPosition = -1, int limit = 50)
    {
        if (afterPosition is < -1 or > 4095 || limit is < 1 or > 100) throw new ConfigurationException("configuration_value_query_invalid");
        var saved = RequireSaved(new(bindingId, resourceId), null);
        if (saved.Revision != revision) throw new ConfigurationException("configuration_revision_conflict");
        var items = saved.Tree.Entries.Select((item, position) => (Item: item, Position: position))
            .Where(value => value.Position > afterPosition && value.Item.Kind == FileObjectKind.File).Take(limit + 1)
            .Select(value => new GenericConfigurationFile(value.Position, value.Item.RelativePath, value.Item.SizeBytes, value.Item.Sha256!)).ToArray();
        var page = items.Take(limit).ToArray();
        return new(Array.AsReadOnly(page), items.Length > limit ? page[^1].Position : null);
    }

    public Stream OpenOriginal(Guid bindingId, string resourceId, long revision, string relativePath, string sourceSha256)
    {
        var saved = RequireSaved(new(bindingId, resourceId), null);
        if (saved.Revision != revision) throw new ConfigurationException("configuration_revision_conflict");
        var entry = saved.Tree.Entries.SingleOrDefault(item => item.RelativePath == relativePath && item.Kind == FileObjectKind.File)
            ?? throw new ConfigurationException("configuration_resource_missing");
        if (entry.Sha256 != sourceSha256) throw new ConfigurationException("configuration_source_changed");
        return _blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
    }

    public GenericConfigurationDocument ReadDocument(Guid bindingId, string resourceId, long revision,
        string relativePath, string sourceSha256, ConfigurationSyntax syntax)
    {
        if (syntax is not (ConfigurationSyntax.Json or ConfigurationSyntax.JsonWithComments or ConfigurationSyntax.Yaml))
            throw new ConfigurationException("configuration_resource_opaque");
        using var input = OpenOriginal(bindingId, resourceId, revision, relativePath, sourceSha256);
        if (input.Length > StrictJson.MaximumBytes) throw new ConfigurationException("configuration_document_limit");
        var bytes = new byte[checked((int)input.Length)];
        try
        {
            input.ReadExactly(bytes);
            var root = new ConfigurationSelector([]);
            using var document = new ConfigurationDocument(new(Guid.NewGuid(), resourceId, revision, sourceSha256),
                syntax, new([new(root, true)], [], []), bytes);
            return new(revision, sourceSha256, document.Read(root));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public ValuePreview Preview(string subject, Guid bindingId, long bindingRevision,
        string relativePath, ConfigurationSyntax syntax, ReadOnlyMemory<byte> patchBytes)
    {
        if (patchBytes.Length is 0 or > StrictJson.MaximumBytes) throw new ConfigurationException("configuration_value_request_limit");
        var owned = patchBytes.ToArray();
        try
        {
            var (request, address, contract, access) = Decode(bindingId, relativePath, syntax, owned);
            _ = RequireSaved(address, bindingRevision);
            using var preview = _patches.PreviewCore(subject, address, bindingRevision, contract, relativePath, syntax, access, request, null);
            return new(request.Binding.OperationId, preview.SizeBytes, preview.HasChanges);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(owned); }
    }

    public ConfigurationSaveResult Apply(string subject, Guid bindingId, long bindingRevision,
        string relativePath, ConfigurationSyntax syntax, ReadOnlyMemory<byte> patchBytes, DateTimeOffset now)
    {
        if (patchBytes.Length is 0 or > StrictJson.MaximumBytes) throw new ConfigurationException("configuration_value_request_limit");
        var owned = patchBytes.ToArray();
        try
        {
            var (request, address, contract, access) = Decode(bindingId, relativePath, syntax, owned);
            var digest = ConfigurationPatchService.SourceDigest(address, bindingRevision, contract, relativePath, syntax, access, request);
            var prior = _patches.ReadAccepted(subject, address, request, digest);
            if (prior is not null) return prior;
            var admission = _resources.TryAcquire(request.Binding.OperationId, ConfigurationRevisionService.Claims(address));
            using var root = admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
                ? "configuration_resource_conflict" : "configuration_host_unavailable");
            prior = _patches.ReadAccepted(subject, address, request, digest);
            if (prior is not null) return prior;
            _ = RequireSaved(address, bindingRevision);
            using var preview = _patches.PreviewCore(subject, address, bindingRevision, contract, relativePath, syntax, access, request, digest);
            return _patches.CommitOwned(preview, root, () => { _ = RequireSaved(address, bindingRevision); }, now, CancellationToken.None);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(owned); }
    }

    private (ConfigurationPatchRequest Request, ConfigurationAddress Address, ConfigurationContractStamp Contract,
        ConfigurationDocumentAccess Access) Decode(Guid bindingId, string relativePath, ConfigurationSyntax syntax, ReadOnlyMemory<byte> bytes)
    {
        _lease.VerifyHeld(_instance);
        if (syntax is not (ConfigurationSyntax.Json or ConfigurationSyntax.JsonWithComments or ConfigurationSyntax.Yaml) ||
            relativePath.Length > 1024) throw new ConfigurationException("configuration_generic_value_request_invalid");
        if (relativePath.Length != 0) PhysicalPathPolicy.ValidateRelativePath(relativePath);
        var request = _reader.Read(bytes);
        var id = request.Binding.ResourceId;
        if (!id.StartsWith("generic-", StringComparison.Ordinal) || !FileTreePolicy.Digest(id["generic-".Length..]))
            throw new ConfigurationException("configuration_generic_definition_unbound");
        var grants = request.Edits.Select(edit => new ConfigurationAccessGrant(edit.Selector, false)).ToImmutableArray();
        // The inverse stamp is used only for exact original-operation lookup; new effects still require the actual saved declaration.
        return (request, new(bindingId, id), new(1, id["generic-".Length..], null, false), ConfigurationPatchPolicy.Freeze(new ConfigurationDocumentAccess(grants, grants, [])));
    }

    private ConfigurationRevision RequireSaved(ConfigurationAddress address, long? bindingRevision)
    {
        var (binding, script) = Definitions(address.BindingId);
        if (bindingRevision is { } expected && binding.Revision != expected)
            throw new ConfigurationException("configuration_binding_revision_conflict");
        var path = script.ConfigurationPaths.SingleOrDefault(item => GenericConfigurationPolicy.ResourceId(item) == address.ResourceId)
            ?? throw new ConfigurationException("configuration_generic_definition_unbound");
        return GenericConfigurationPolicy.RequireSaved(binding, script, path, _revisions);
    }

    private (AutomationBinding Binding, AutomationScript Script) Definitions(Guid bindingId)
    {
        _lease.VerifyHeld(_instance);
        var catalog = _definitions.Read();
        var binding = catalog.Bindings.SingleOrDefault(item => item.Id == bindingId)
            ?? throw new ConfigurationException("configuration_binding_missing");
        var script = Core.Automation.BindingScriptSource.Resolve(catalog.Scripts.Single(item => item.Id == binding.ScriptId), binding);
        if (script.Kind != AutomationKind.Generic) throw new ConfigurationException("configuration_generic_definition_unbound");
        return (binding, script);
    }
}
