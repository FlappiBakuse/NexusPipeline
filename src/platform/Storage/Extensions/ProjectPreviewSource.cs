using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ProjectPreviewSource(Guid OwnerId, bool IsScript, long OwnerRevision, long ScriptRevision,
    Guid RootId, long RootRevision, PhysicalObjectIdentity RootIdentity, ExtensionInstalledMetadata Consumer,
    ExtensionInstalledMetadata Provider, ImmutableArray<ConfigurationReadStamp> ReadSet,
    ImmutableArray<CompiledProjectAsset> Assets, string Fingerprint, bool CanExecute, string? PreviewIndex)
{
    internal static ProjectPreviewSource Binding(CompiledProjectSnapshot snapshot)
    {
        var value = snapshot.Value;
        return new(value.BindingId, false, value.BindingRevision, value.ScriptRevision, value.RootId, value.RootRevision,
            value.RootIdentity, value.Consumer.Installed, value.Provider.Installed, value.ReadSet, value.Assets,
            snapshot.Document.Sha256, value.CanExecute, value.PreviewIndex);
    }

    [SupportedOSPlatform("windows")]
    internal static ProjectPreviewSource Script(RuntimeInstance instance, ContractReader contracts, SelectedProjectSource source,
        FrozenCompilation graph, ProjectCompilerInstallation consumer, ProjectCompilerInstallation provider, JsonElement selection,
        CancellationToken cancellation)
    {
        if (!source.IsScriptAuthority) throw new IOException("project_script_authority_required");
        var blobs = new ImmutableBlobStore(instance, contracts);
        var contents = graph.Persist(blobs, cancellation).ToDictionary(item => item.Name, item => item.Content, StringComparer.Ordinal);
        var assets = graph.Assets.Select(item => new CompiledProjectAsset(item.Name, item.Kind, contents[item.Name], item.References)).ToImmutableArray();
        var document = blobs.PersistPlan(BoundedJson.Serialize(new
        {
            format = "nexus.project.script-preview", version = 1, instanceId = instance.InstanceId,
            scriptId = source.Script.Id, scriptRevision = source.Script.Revision, rootId = source.Root.Id,
            rootRevision = source.Root.Revision, rootIdentity = source.RootIdentity, consumer = consumer.Metadata,
            provider = provider.Metadata, selection, assets, graph.PreviewIndex
        }));
        return new(source.Script.Id, true, source.Script.Revision, source.Script.Revision, source.Root.Id, source.Root.Revision,
            source.RootIdentity, consumer.Metadata, provider.Metadata, [], assets, document.Sha256, false, graph.PreviewIndex);
    }
}
