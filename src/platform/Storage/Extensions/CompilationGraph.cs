using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record FrozenCompiledAsset(string Name, CompiledAssetKind Kind, byte[] Content, string Sha256, ImmutableArray<string> References);
internal sealed record FrozenCompilation(string RootIndex, ImmutableArray<FrozenCompiledAsset> Assets, JsonElement ExecutionView,
    ImmutableArray<SourceResourceTree> ResourceTrees, bool CanExecute, string? PreviewIndex)
{
    internal ImmutableArray<ProjectProtectedInput> ProtectedInputs { get; init; } = [];
    internal ImmutableArray<string> InitializationOccurrences { get; init; } = [];
    internal ImmutableArray<(string Name, BlobReference Content)> Persist(ImmutableBlobStore blobs, CancellationToken cancellation)
    {
        var result = ImmutableArray.CreateBuilder<(string, BlobReference)>();
        foreach (var asset in Assets)
        {
            cancellation.ThrowIfCancellationRequested();
            var reference = new BlobReference($"blobs/content/{asset.Sha256}.bin", asset.Content.LongLength, asset.Sha256);
            using var stream = new MemoryStream(asset.Content, writable: false);
            blobs.PersistContent(stream, reference, cancellation); result.Add((asset.Name, reference));
        }
        return result.ToImmutable();
    }
}

internal static class CompilationGraph
{
    internal static FrozenCompilation Freeze(CompilationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var resources = (result.ResourceTrees ?? []).Select(item => item is null ? throw Invalid() : item with { IncludedPaths = item.IncludedPaths?.Order(StringComparer.Ordinal).ToArray() }).ToImmutableArray();
        if (resources.Length > 8 || resources.Any(item => !Name(item.Name) || item.Path is null || item.Path.Length == 0 && item.IncludedPaths is null) ||
            resources.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != resources.Length ||
            resources.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != resources.Length) throw Invalid();
        foreach (var resource in resources) ProjectSubsetPolicy.Require(resource.Path, resource.IncludedPaths);
        if (result.Assets is null || result.Assets.Count is < 1 or > 4112 || result.ExecutionView.ValueKind != JsonValueKind.Object) throw Invalid();
        var view = JsonSerializer.SerializeToUtf8Bytes(result.ExecutionView);
        if (view.Length > 262144) throw Invalid();
        using var parsedView = StrictJson.Parse(view);
        var assets = ImmutableArray.CreateBuilder<FrozenCompiledAsset>(); long bytes = 0; var references = 0;
        foreach (var asset in result.Assets)
        {
            if (asset is null || !Name(asset.Name) || !Enum.IsDefined(asset.Kind) || asset.References is null || asset.References.Count > 4096 ||
                asset.References.Any(item => !Name(item)) || asset.References.Distinct(StringComparer.Ordinal).Count() != asset.References.Count ||
                (references += asset.References.Count) > 8192 || asset.Content.Length < 1 || (bytes += asset.Content.Length) > 32 * 1024 * 1024)
                throw Invalid();
            var limit = asset.Kind switch { CompiledAssetKind.Page => 65536, CompiledAssetKind.Index => 524288, _ => 32 * 1024 * 1024 };
            if (asset.Content.Length > limit) throw Invalid();
            var content = asset.Content.ToArray();
            if (asset.Kind != CompiledAssetKind.Artifact) { using var parsed = StrictJson.Parse(content); }
            assets.Add(new(asset.Name, asset.Kind, content, Convert.ToHexStringLower(SHA256.HashData(content)), asset.References.ToImmutableArray()));
        }
        var frozen = assets.ToImmutable();
        if (frozen.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != frozen.Length ||
            frozen.Count(item => item.Kind == CompiledAssetKind.Artifact) > 8 || frozen.Count(item => item.Kind == CompiledAssetKind.Index) > 8 ||
            frozen.Count(item => item.Kind == CompiledAssetKind.Page) > 4096) throw Invalid();
        var byName = frozen.ToDictionary(item => item.Name, StringComparer.Ordinal);
        if (!Name(result.RootIndex) || !byName.TryGetValue(result.RootIndex, out var root) || root.Kind != CompiledAssetKind.Index ||
            frozen.SelectMany(item => item.References).Any(name => !byName.ContainsKey(name))) throw Invalid();
        RequireClosed(result.RootIndex, byName.ToDictionary(item => item.Key, item => item.Value.References, StringComparer.Ordinal));
        _ = RequirePreview(result.PreviewIndex, frozen.Select(item => (item.Name, item.Kind, item.References)));
        var protectedInputs = (result.ProtectedInputs ?? []).Select(item => item is null ? throw Invalid() :
            new ProjectProtectedInput(item.Key, item.OccurrenceIds?.ToArray() ?? throw Invalid())).ToImmutableArray();
        if (protectedInputs.Length > 32 || protectedInputs.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != protectedInputs.Length ||
            protectedInputs.Any(item => item.Key is not { Length: > 0 and <= 80 } || item.Key.Any(value => value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
                item.OccurrenceIds.Count is < 1 or > 4096 || item.OccurrenceIds.Distinct(StringComparer.Ordinal).Count() != item.OccurrenceIds.Count ||
                item.OccurrenceIds.Any(id => id is not { Length: > 0 and <= 120 } || id.Any(char.IsControl)))) throw Invalid();
        return new(result.RootIndex, frozen, parsedView.RootElement.Clone(), resources, result.CanExecute, result.PreviewIndex)
            { ProtectedInputs = protectedInputs, InitializationOccurrences = FreezeInitialization(result.InitializationOccurrences ?? []) };
    }

    internal static ImmutableArray<string> FreezeInitialization(IReadOnlyList<string> occurrences)
    {
        if (occurrences.Count > 128 || occurrences.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 120 || id.Any(char.IsControl)) ||
            occurrences.Distinct(StringComparer.Ordinal).Count() != occurrences.Count) throw Invalid();
        return occurrences.ToImmutableArray();
    }

    internal static ImmutableHashSet<string> RequirePreview(string? root,
        IEnumerable<(string Name, CompiledAssetKind Kind, ImmutableArray<string> References)> assets)
    {
        if (root is null) return ImmutableHashSet<string>.Empty;
        var byName = assets.ToDictionary(item => item.Name, StringComparer.Ordinal);
        if (!Name(root) || !byName.TryGetValue(root, out var index) || index.Kind != CompiledAssetKind.Index) throw Invalid();
        var visited = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var name))
        {
            if (!visited.Add(name)) continue;
            if (!byName.TryGetValue(name, out var asset) || asset.Kind is not (CompiledAssetKind.Index or CompiledAssetKind.Page)) throw Invalid();
            foreach (var reference in asset.References) pending.Push(reference);
        }
        return visited.ToImmutable();
    }

    internal static void RequireMetadata(string root, ImmutableArray<CompiledProjectAsset> assets)
    {
        if (assets.IsDefaultOrEmpty || assets.Length > 4112 || assets.Any(item => item is null || !Name(item.Name) ||
            !Enum.IsDefined(item.Kind) || item.References.IsDefault || item.References.Length > 4096 || item.References.Any(value => !Name(value)) ||
            item.References.Distinct(StringComparer.Ordinal).Count() != item.References.Length ||
            item.Content.SizeBytes is < 1 or > 32 * 1024 * 1024 ||
            item.Kind == CompiledAssetKind.Page && item.Content.SizeBytes > 65536 ||
            item.Kind == CompiledAssetKind.Index && item.Content.SizeBytes > 524288) ||
            assets.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != assets.Length ||
            assets.Count(item => item.Kind == CompiledAssetKind.Artifact) > 8 || assets.Count(item => item.Kind == CompiledAssetKind.Index) > 8 ||
            assets.Count(item => item.Kind == CompiledAssetKind.Page) > 4096 || assets.Sum(item => item.Content.SizeBytes) > 32 * 1024 * 1024 ||
            assets.Sum(item => item.References.Length) > 8192) throw Invalid();
        var byName = assets.ToDictionary(item => item.Name, StringComparer.Ordinal);
        if (!Name(root) || !byName.TryGetValue(root, out var index) || index.Kind != CompiledAssetKind.Index ||
            assets.SelectMany(item => item.References).Any(name => !byName.ContainsKey(name))) throw Invalid();
        RequireClosed(root, byName.ToDictionary(item => item.Key, item => item.Value.References, StringComparer.Ordinal));
    }

    private static void RequireClosed(string root, IReadOnlyDictionary<string, ImmutableArray<string>> assets)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string Name, bool Exit)>(); pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            if (item.Exit) { visiting.Remove(item.Name); visited.Add(item.Name); continue; }
            if (visited.Contains(item.Name)) continue;
            if (!visiting.Add(item.Name)) throw Invalid();
            pending.Push((item.Name, true));
            foreach (var name in assets[item.Name].Reverse()) pending.Push((name, false));
        }
        if (visited.Count != assets.Count) throw Invalid();
    }

    private static bool Name(string name) => !string.IsNullOrEmpty(name) && name.Length <= 128 && name[0] is >= 'a' and <= 'z' &&
        name.All(value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
    private static IOException Invalid() => new("compilation_graph_invalid");
}
