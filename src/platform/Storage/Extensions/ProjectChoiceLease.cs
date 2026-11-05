using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ProjectChoiceLease : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly StateDatabase _database;
    private readonly ConfigurationRevisionStore _revisions;
    private readonly StoredConfigurationRevision _saved;
    private readonly ResourceOperationLease _root;
    private readonly List<IDisposable> _held = [];
    private readonly long _bindingRevision;
    private readonly string _extensionId;
    private long _catalogRevision;
    private bool _disposed;
    internal JsonElement Selection { get; }
    internal CompiledProjectChoice Proof { get; }

    internal ProjectChoiceLease(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ContractReader contracts, Guid bindingId, long bindingRevision, string extensionId)
    {
        _instance = instance; _database = database; _lease = lease; _revisions = new(instance, database, contracts);
        _bindingRevision = bindingRevision; _extensionId = extensionId;
        lease.VerifyHeld(instance); var address = ProjectChoiceStore.Address(bindingId);
        _root = resources.TryAcquire(Guid.NewGuid(), [new(ConfigurationRevisionService.ResourceKey(address), ResourceClaimMode.Reference),
            new("automation:definitions", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)]).Lease
            ?? throw new IOException("project_selection_unavailable");
        try
        {
            _saved = _revisions.ReadStored(address) ?? throw new IOException("project_selection_required");
            var entry = _saved.Revision.Tree.Entries.SingleOrDefault(item => item.RelativePath == "" && item.Kind == FileObjectKind.File);
            if (_saved.Revision.Tree.Shape != FileObjectKind.File || entry is null || entry.SizeBytes is < 1 or > 65536)
                throw new IOException("project_selection_invalid");
            var blobs = new ImmutableBlobStore(instance, contracts);
            var content = new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!);
            _held.Add(blobs.OpenVerified(_saved.Manifest));
            var input = blobs.OpenVerified(content); _held.Add(input);
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            using var parsed = StrictJson.Parse(bytes);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("project_selection_invalid");
            Selection = parsed.RootElement.Clone();
            Proof = new(new(address, _saved.Revision.Revision, bindingRevision, _saved.Revision.Contract, "", content.Sha256), _saved.Manifest, content);
            _catalogRevision = new AutomationDefinitionStore(_database).ReadRevision();
            RequireCurrent();
        }
        catch { Dispose(); throw; }
    }

    internal void RequireProject(CompiledProjectDocument project)
    {
        RequireCurrent();
        if (project.BindingId != Proof.Source.Address.BindingId || project.BindingRevision != _bindingRevision ||
            project.Consumer.Installed.ExtensionId != _extensionId || ProjectChoiceStore.Contract(project) != _saved.Revision.Contract ||
            !JsonElement.DeepEquals(project.Selection, Selection)) throw new IOException("project_selection_contract_changed");
    }

    internal void RequireLive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _lease.VerifyHeld(_instance);
        if (!_root.IsActive) throw new IOException("project_selection_unavailable");
    }

    internal void RequireCurrent()
    {
        RequireLive();
        var revision = new AutomationDefinitionStore(_database).ReadRevision();
        if (revision != _catalogRevision)
        {
            var catalog = new AutomationDefinitionStore(_database).Read();
            var binding = catalog.Bindings.SingleOrDefault(item => item.Id == _saved.Revision.Address.BindingId);
            var script = catalog.Scripts.SingleOrDefault(item => item.Id == binding?.ScriptId);
            if (binding?.Revision != _bindingRevision || script?.ExtensionId != _extensionId ||
                script.AdapterResources.Any(item => item.ResourceId == ProjectChoiceStore.ResourceId)) throw new IOException("project_selection_owner_changed");
            _catalogRevision = revision;
        }
        if (_revisions.ReadCurrentRevision(_saved.Revision.Address) != _saved.Revision.Revision)
            throw new IOException("project_selection_changed");
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _root.Dispose();
    }
}
