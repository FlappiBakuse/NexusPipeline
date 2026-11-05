using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record CompiledProjectAsset(string Name, CompiledAssetKind Kind, BlobReference Content, ImmutableArray<string> References);
internal sealed record CompiledSourceFile(string Path, PhysicalObjectIdentity Identity, BlobReference Content);
internal sealed record CompiledResourceTree(string Name, string Path, BlobReference Archive)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<string>? IncludedPaths { get; init; }
}
internal sealed record CompiledConfigMount(SourceConfiguration Configuration, BlobReference Snapshot);
internal sealed record CompiledProjectSecret(string Key, SecretFieldReference Reference, ImmutableArray<string> OccurrenceIds);
internal sealed record CompiledInstallation(ExtensionInstalledMetadata Installed, BlobReference Manifest);
internal sealed record CompiledProjectDocument(string Format, int Version, Guid InstanceId, Guid BindingId,
    long BindingRevision, long ScriptRevision, Guid RootId, long RootRevision, PhysicalObjectIdentity RootIdentity,
    CompiledInstallation Consumer, CompiledInstallation Provider, string CompilerId, JsonElement Selection,
    ImmutableArray<ProjectDirectoryRead> Directories, ImmutableArray<CompiledSourceFile> SourceFiles,
    ImmutableArray<CompiledConfigMount> Configurations, ImmutableArray<ConfigurationReadStamp> ReadSet,
    ImmutableArray<CapturedConfigFile> ConfigurationFiles, ImmutableArray<CompiledResourceTree> ResourceTrees,
    string RootIndex, ImmutableArray<CompiledProjectAsset> Assets, JsonElement ExecutionView)
{
    public ImmutableArray<BlobReference> Contents { get; init; } = [];
    public CompiledProjectChoice? Choice { get; init; }
    public bool CanExecute { get; init; } = true;
    public string? PreviewIndex { get; init; }
    public ImmutableArray<CompiledProjectSecret> ProtectedInputs { get; init; } = [];
    public ImmutableArray<string> InitializationOccurrences { get; init; } = [];
}

internal sealed record CompiledProjectSnapshot(BlobReference Document, CompiledProjectDocument Value,
    ImmutableArray<BlobReference> Closure)
{
    internal CompiledInput Wire => new() { Fingerprint = Document.Sha256, CanExecute = Value.CanExecute,
        View = Value.ExecutionView.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal) };
    internal static CompiledProjectSnapshot Persist(ImmutableBlobStore blobs, CompiledProjectDocument document,
        IReadOnlyList<BlobReference> resourceContents, IReadOnlyList<BlobReference> configurationContents, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var choice = document.Choice is { } selected ? new[] { selected.Snapshot, selected.Content } : [];
        var closure = document.Assets.Select(item => item.Content).Concat(document.SourceFiles.Select(item => item.Content))
            .Concat(document.ConfigurationFiles.Select(item => item.Content)).Concat(document.Configurations.Select(item => item.Snapshot))
            .Concat(document.ResourceTrees.Select(item => item.Archive)).Concat(resourceContents).Concat(configurationContents).Concat(choice)
            .Concat([document.Consumer.Installed.Footprint, document.Consumer.Manifest,
                document.Provider.Installed.Footprint, document.Provider.Manifest])
            .Distinct().OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToImmutableArray();
        document = document with { Contents = closure };
        var reference = blobs.PersistPlan(BoundedJson.Serialize(document));
        foreach (var item in closure) { cancellation.ThrowIfCancellationRequested(); blobs.Verify(item); }
        return new(reference, document, closure.Add(reference));
    }
}
