using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Archives;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record UpdateRecipeFile(string RelativePath, long SizeBytes, string Sha256);
internal sealed record UpdateRecipeBase(string ReleaseTag, long ReleaseId, long AssetId, string AssetSha256, string ManifestSha256);

internal sealed record UpdateRecipe(UpdateRecipeBase? Base, ImmutableArray<UpdateRecipeFile> Included,
    ImmutableArray<UpdateRecipeFile> Reused, ImmutableArray<UpdateRecipeFile> Removed, UpdateRecipeFile ManifestFile)
{
    internal const string Path = "package-info/recipe.json";
    internal static ImmutableArray<string> Metadata { get; } = [Path, "package-info/README.zh-CN.md", "package-info/README.en-US.md"];

    internal static UpdateRecipe Read(PayloadArchive archive, UpdatePayloadManifest manifest, ContractReader contracts)
    {
        var bytes = archive.ReadDocument(Path, StrictJson.MaximumBytes) ?? throw Invalid();
        using var document = contracts.Read(bytes, "update-recipe.schema.json");
        var root = document.RootElement; var target = root.GetProperty("target");
        var actualManifest = archive.Files.Single(file => file.RelativePath == ApplicationTree.ManifestPath);
        if (root.GetProperty("packageKind").GetString() != "update" || root.GetProperty("bundledFiles").GetArrayLength() != 0 ||
            target.GetProperty("version").GetString() != manifest.HostVersion ||
            target.GetProperty("buildId").GetString() != manifest.Application.BuildId ||
            target.GetProperty("frontendHash").GetString() != manifest.Application.FrontendHash ||
            target.GetProperty("payloadManifestSha256").GetString() != actualManifest.Sha256) throw Invalid();
        var capabilities = root.GetProperty("requirements").GetProperty("requiredCapabilities").EnumerateArray().Select(item => item.GetString());
        if (!capabilities.SequenceEqual(new[] { "payload-manifest-v2", "single-delta-v1", "transaction-recovery-v1" })) throw Invalid();
        var baseValue = root.GetProperty("base");
        var basis = baseValue.ValueKind == JsonValueKind.Null ? null : new UpdateRecipeBase(
            baseValue.GetProperty("releaseTag").GetString()!, baseValue.GetProperty("releaseId").GetInt64(),
            baseValue.GetProperty("assetId").GetInt64(), baseValue.GetProperty("assetSha256").GetString()!,
            baseValue.GetProperty("payloadManifestSha256").GetString()!);
        var included = ReadFiles(root.GetProperty("includedFiles"));
        var reused = ReadFiles(root.GetProperty("reusedFiles"));
        var removed = ReadFiles(root.GetProperty("removedFiles"));
        var all = included.Concat(reused).Concat(removed).ToArray();
        if (all.Length > 4096 || all.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length ||
            basis is null && (!reused.IsEmpty || !removed.IsEmpty)) throw Invalid();
        var expected = manifest.Files.Select(file => new UpdateRecipeFile(file.RelativePath, file.SizeBytes, file.Sha256))
            .Append(new(ApplicationTree.ManifestPath, actualManifest.SizeBytes, actualManifest.Sha256)).OrderBy(file => file.RelativePath, StringComparer.Ordinal);
        if (!included.Concat(reused).OrderBy(file => file.RelativePath, StringComparer.Ordinal).SequenceEqual(expected) ||
            !included.Any(file => file.RelativePath == ApplicationTree.ManifestPath)) throw Invalid();
        var manual = root.GetProperty("manualCover");
        if (manual.GetProperty("allowed").GetBoolean() && (basis is null || !removed.IsEmpty ||
            !manual.GetProperty("exactBaseRequired").GetBoolean() || !manual.GetProperty("requiresPostStartReconciliation").GetBoolean())) throw Invalid();
        var result = new UpdateRecipe(basis, included, reused, removed,
            new(ApplicationTree.ManifestPath, actualManifest.SizeBytes, actualManifest.Sha256));
        result.RequireArchive(archive);
        return result;
    }

    private static ImmutableArray<UpdateRecipeFile> ReadFiles(JsonElement entries)
    {
        var files = entries.EnumerateArray().Select(item => new UpdateRecipeFile(item.GetProperty("path").GetString()!,
            item.GetProperty("sizeBytes").GetInt64(), item.GetProperty("sha256").GetString()!)).ToImmutableArray();
        if (files.Sum(file => file.SizeBytes) > 536870912) throw Invalid();
        foreach (var file in files)
        {
            PhysicalPathPolicy.ValidateRelativePath(file.RelativePath);
            if (file.RelativePath != ApplicationTree.ManifestPath) _ = ApplicationManifest.Kind(file.RelativePath);
        }
        return files;
    }

    private void RequireArchive(PayloadArchive archive)
    {
        var metadata = archive.Files.Where(file => file.RelativePath.StartsWith("package-info/", StringComparison.Ordinal)).ToArray();
        if (!metadata.Select(file => file.RelativePath).Order(StringComparer.Ordinal).SequenceEqual(Metadata.Order(StringComparer.Ordinal)) ||
            metadata.Any(file => file.SizeBytes is < 1 or > 2097152)) throw Invalid();
        var actual = archive.Files.Where(file => !file.RelativePath.StartsWith("package-info/", StringComparison.Ordinal))
            .Select(file => new UpdateRecipeFile(file.RelativePath, file.SizeBytes, file.Sha256)).OrderBy(file => file.RelativePath, StringComparer.Ordinal);
        if (!actual.SequenceEqual(Included.OrderBy(file => file.RelativePath, StringComparer.Ordinal))) throw Invalid();
        var directories = archive.Files.SelectMany(file => ApplicationManifest.Ancestors(file.RelativePath)).Append("").ToHashSet(StringComparer.Ordinal);
        if (!directories.SetEquals(archive.Directories)) throw Invalid();
    }

    internal void RequireBefore(ArchivedFileTree before, UpdatePayloadManifest manifest, HostUpdateFeed feed,
        ImmutableBlobStore blobs, ContractReader contracts)
    {
        var original = new UpdateManifestCodec(contracts).RequireOriginal(before, blobs, manifest.Profile);
        if (Base is null)
        {
            if (!Reused.IsEmpty || !Removed.IsEmpty) throw Invalid();
            return;
        }
        var actual = before.Snapshot.Entries.Single(entry => entry.RelativePath == ApplicationTree.ManifestPath);
        var release = feed.Releases.SingleOrDefault(item => item.HostVersion == original.HostVersion) ?? throw Invalid();
        if (Base.ReleaseTag != "v" + original.HostVersion || Base.ManifestSha256 != actual.Sha256 ||
            Base.ManifestSha256 != release.PayloadManifestSha256 || Base.AssetSha256 != release.Artifact.Sha256 ||
            Base.ReleaseId != release.ReleaseId || Base.AssetId != release.AssetId ||
            AutomaticUpdatePolicy.CompareVersions(original.HostVersion, "0.18.0") < 0 ||
            AutomaticUpdatePolicy.CompareVersions(manifest.HostVersion, original.HostVersion) <= 0) throw Invalid();
        var previous = original.Files.Select(file => new UpdateRecipeFile(file.RelativePath, file.SizeBytes, file.Sha256))
            .Append(new(ApplicationTree.ManifestPath, actual.SizeBytes, actual.Sha256)).ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var reused in Reused)
            if (!previous.TryGetValue(reused.RelativePath, out var expected) || reused != expected ||
                !before.Snapshot.Entries.Any(entry => entry.RelativePath == reused.RelativePath && entry.Kind == FileObjectKind.File &&
                    entry.SizeBytes == reused.SizeBytes && entry.Sha256 == reused.Sha256)) throw Invalid();
        var target = manifest.Files.Select(file => file.RelativePath).Append(ApplicationTree.ManifestPath).ToHashSet(StringComparer.Ordinal);
        var removed = previous.Values.Where(file => !target.Contains(file.RelativePath)).OrderBy(file => file.RelativePath, StringComparer.Ordinal);
        if (!removed.SequenceEqual(Removed.OrderBy(file => file.RelativePath, StringComparer.Ordinal))) throw Invalid();
    }

    internal ArchivedFileTree Capture(PayloadArchive archive, ArchivedFileTree before, UpdatePayloadManifest manifest,
        HostUpdateFeed feed, ImmutableBlobStore blobs, ContractReader contracts, CancellationToken cancellation)
    {
        RequireBefore(before, manifest, feed, blobs, contracts);
        var partial = archive.CaptureCandidate(blobs, cancellation);
        var contents = new List<BlobReference>();
        foreach (var file in Included)
            contents.Add(partial.Contents.SingleOrDefault(blob => blob.Sha256 == file.Sha256 && blob.SizeBytes == file.SizeBytes) ?? throw Invalid());
        foreach (var file in Reused)
            contents.Add(before.Contents.SingleOrDefault(blob => blob.Sha256 == file.Sha256 && blob.SizeBytes == file.SizeBytes) ?? throw Invalid());
        var files = manifest.Files.Select(file => new FileTreeEntry(file.RelativePath, FileObjectKind.File, file.SizeBytes, file.Sha256))
            .Append(new(ManifestFile.RelativePath, FileObjectKind.File, ManifestFile.SizeBytes, ManifestFile.Sha256)).ToArray();
        var directories = files.SelectMany(file => ApplicationManifest.Ancestors(file.RelativePath)).Append("").Distinct(StringComparer.Ordinal)
            .Select(path => new FileTreeEntry(path, FileObjectKind.Directory, 0, null));
        var entries = files.Concat(directories).OrderBy(entry => entry.RelativePath,
            Comparer<string>.Create((left, right) => System.Text.Encoding.UTF8.GetBytes(left).AsSpan()
                .SequenceCompareTo(System.Text.Encoding.UTF8.GetBytes(right)))).ToArray();
        var result = new ArchivedFileTree(new(FileObjectKind.Directory, Array.AsReadOnly(entries)), [], Array.AsReadOnly(contents.Distinct().ToArray()));
        ApplicationManifest.RequireTree(result, manifest.Application,
            new($"blobs/content/{ManifestFile.Sha256}.bin", ManifestFile.SizeBytes, ManifestFile.Sha256), allowMissing: false);
        return result;
    }

    internal static IOException Invalid() => new("update_recipe_unbound");
}
