using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
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
    internal ImmutableDictionary<string, byte[]> Contents { get; init; } = ImmutableDictionary<string, byte[]>.Empty;
    internal long SizeBytes => Contents.Values.Sum(item => item.LongLength);

    [SupportedOSPlatform("windows")]
    internal static ProjectPreviewSource Script(SelectedProjectSource source,
        FrozenCompilation graph, ProjectCompilerInstallation consumer, ProjectCompilerInstallation provider, JsonElement selection,
        CancellationToken cancellation)
    {
        if (!source.IsScriptAuthority) throw new IOException("project_script_authority_required");
        cancellation.ThrowIfCancellationRequested();
        return Create(source, graph, consumer, provider, selection, [], false);
    }

    [SupportedOSPlatform("windows")]
    internal static ProjectPreviewSource Create(SelectedProjectSource source, FrozenCompilation graph,
        ProjectCompilerInstallation consumer, ProjectCompilerInstallation provider, JsonElement selection,
        ImmutableArray<ConfigurationReadStamp> readSet, bool canExecute)
    {
        var assets = graph.Assets.Select(item => new CompiledProjectAsset(item.Name, item.Kind,
            new($"blobs/content/{item.Sha256}.bin", item.Content.LongLength, item.Sha256), item.References)).ToImmutableArray();
        var names = CompilationGraph.RequirePreview(graph.PreviewIndex, assets.Select(item => (item.Name, item.Kind, item.References)));
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
        {
            source = source.Fingerprint(), consumer = consumer.Metadata, provider = provider.Metadata,
            selection, readSet, assets, graph.RootIndex, graph.ExecutionView, graph.CanExecute, graph.PreviewIndex,
            graph.ResourceTrees, graph.ProtectedInputs, graph.InitializationOccurrences
        })));
        return new(source.IsScriptAuthority ? source.Script.Id : source.Binding.Id, source.IsScriptAuthority,
            source.IsScriptAuthority ? source.Script.Revision : source.Binding.Revision, source.Script.Revision,
            source.Root.Id, source.Root.Revision, source.RootIdentity, consumer.Metadata, provider.Metadata, readSet,
            assets, fingerprint, canExecute, graph.PreviewIndex)
            { Contents = graph.Assets.Where(item => names.Contains(item.Name)).ToImmutableDictionary(item => item.Name,
                item => item.Content.ToArray(), StringComparer.Ordinal) };
    }
}
