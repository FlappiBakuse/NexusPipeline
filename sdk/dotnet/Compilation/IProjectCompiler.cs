using System.Text.Json;

namespace NexusPipeline.Extensibility.Compilation;

/// <summary>Pure project compilation uses only the Host's selected readonly source.</summary>
public interface IProjectCompiler
{
    /// <summary>Provider-specific choices remain ordinary bounded JSON and carry no source authority.</summary>
    ValueTask<JsonElement> DiscoverAsync(ISelectedSource source, JsonElement selection, CancellationToken cancellationToken);
    /// <summary>Returns complete retained content plus a bounded execution view from the same compilation.</summary>
    ValueTask<CompilationResult> CompileAsync(ISelectedSource source, JsonElement selection, CancellationToken cancellationToken);
}

/// <summary>Content roles give the Host independent limits for complete artifacts and browser pages.</summary>
public enum CompiledAssetKind
{
    /// <summary>Complete retained content, bounded by the compilation's total byte budget.</summary>
    Artifact,
    /// <summary>A bounded entry index from which all declared content is reachable.</summary>
    Index,
    /// <summary>A bounded browser page; it cannot name content outside the closed result.</summary>
    Page
}
/// <summary>References name assets in this result only; hashes and persistent authority are assigned by the Host.</summary>
public sealed record CompiledAsset(string Name, CompiledAssetKind Kind, ReadOnlyMemory<byte> Content, IReadOnlyList<string> References);
/// <summary>The complete closed content graph is retained; execution never hydrates every schema page.</summary>
public sealed record CompilationResult(string RootIndex, IReadOnlyList<CompiledAsset> Assets, JsonElement ExecutionView)
{
    /// <summary>False retains complete readonly data for discovery and repair while forbidding execution acceptance.</summary>
    public bool CanExecute { get; init; } = true;
    /// <summary>Only the reachable Index/Page subgraph is public; null exposes no browser preview.</summary>
    public string? PreviewIndex { get; init; }
    /// <summary>The Host independently archives complete selected resource trees with its existing file-tree owner.</summary>
    public IReadOnlyList<SourceResourceTree>? ResourceTrees { get; init; }
    /// <summary>Declares exact protected fields used by named task occurrences; it grants no secret access.</summary>
    public IReadOnlyList<ProjectProtectedInput>? ProtectedInputs { get; init; }
    /// <summary>Ordered technical occurrences available only to an explicit initialization request; never automatic business work.</summary>
    public IReadOnlyList<string>? InitializationOccurrences { get; init; }
}
/// <summary>A stable nonsecret field key and its finite occurrence uses, independently bound by the Host.</summary>
public sealed record ProjectProtectedInput(string Key, IReadOnlyList<string> OccurrenceIds);
/// <summary>A selected tree or finite subset carries no native execution or arbitrary-root authority.</summary>
public sealed record SourceResourceTree(string Name, string Path)
{
    /// <summary>Null captures the complete tree; a nonempty relative selection permits Path to be the selected script root.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? IncludedPaths { get; init; }
}
