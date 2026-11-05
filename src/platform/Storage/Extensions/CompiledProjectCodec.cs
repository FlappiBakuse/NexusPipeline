using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class CompiledProjectCodec
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static CompiledProjectSnapshot Read(RuntimeInstance instance, ImmutableBlobStore blobs, BlobReference reference, bool verifyContents = true)
    {
        var value = ReadDocument<CompiledProjectDocument>(blobs, reference);
        if (value.Format != "nexus.compiled-project" || value.Version != 1 || value.InstanceId != instance.InstanceId ||
            value.BindingId == Guid.Empty || value.RootId == Guid.Empty || value.BindingRevision < 1 || value.ScriptRevision < 1 || value.RootRevision < 1 ||
            value.Selection.ValueKind != JsonValueKind.Object || value.ExecutionView.ValueKind != JsonValueKind.Object ||
            value.Directories.IsDefault || value.Directories.Length > 128 || value.Directories.Sum(item => item.Entries.Length) > 4096 ||
            value.SourceFiles.IsDefault || value.ConfigurationFiles.IsDefault || value.SourceFiles.Length + value.ConfigurationFiles.Length > 128 ||
            value.ResourceTrees.IsDefault || value.ResourceTrees.Length > 8 || value.Configurations.IsDefault || value.Configurations.Length > 32 ||
            value.Contents.IsDefaultOrEmpty || value.Contents.Distinct().Count() != value.Contents.Length) throw Invalid();
        _ = BoundedJson.Serialize(value.Selection, null, 65536); _ = BoundedJson.Serialize(value.ExecutionView, null, 262144);
        CompilationGraph.RequireMetadata(value.RootIndex, value.Assets);
        if (value.InitializationOccurrences.IsDefault) throw Invalid();
        _ = CompilationGraph.FreezeInitialization(value.InitializationOccurrences);
        if (value.ProtectedInputs.IsDefault || value.ProtectedInputs.Length > 32 ||
            value.ProtectedInputs.Select(input => input.Key).Distinct(StringComparer.Ordinal).Count() != value.ProtectedInputs.Length ||
            value.ProtectedInputs.Any(input => input.Key is not { Length: > 0 and <= 80 } || input.Reference is null ||
                input.Key.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
                input.OccurrenceIds.IsDefaultOrEmpty || input.OccurrenceIds.Length > 4096 ||
                input.OccurrenceIds.Any(id => id is not { Length: > 0 and <= 120 } || id.Any(char.IsControl)) ||
                input.OccurrenceIds.Distinct(StringComparer.Ordinal).Count() != input.OccurrenceIds.Length)) throw Invalid();
        _ = CompilationGraph.RequirePreview(value.PreviewIndex, value.Assets.Select(item => (item.Name, item.Kind, item.References)));
        ConfigurationReadPolicy.Freeze(value.ReadSet);
        var required = RequiredContents(value, blobs);
        if (!required.SequenceEqual(value.Contents)) throw Invalid();
        if (value.Choice is { } choice && (choice.Source.Address != ProjectChoiceStore.Address(value.BindingId) ||
            choice.Source.Contract != ProjectChoiceStore.Contract(value) || !value.ReadSet.Contains(choice.Source))) throw Invalid();
        foreach (var asset in value.Assets)
        {
            Content(asset.Content);
            if (verifyContents && asset.Kind != CompiledAssetKind.Artifact)
            { using var input = blobs.OpenVerified(asset.Content); using var parsed = StrictJson.Parse(ReadBytes(input)); }
        }
        if (verifyContents) foreach (var item in required) blobs.Verify(item);
        return new(reference, value, required.Add(reference));
    }

    private static ImmutableArray<BlobReference> RequiredContents(CompiledProjectDocument value, ImmutableBlobStore blobs)
    {
        var references = value.Assets.Select(item => item.Content).Concat(value.SourceFiles.Select(item => item.Content))
            .Concat(value.ConfigurationFiles.Select(item => item.Content)).Concat(value.Configurations.Select(item => item.Snapshot))
            .Concat(value.ResourceTrees.Select(item => item.Archive)).ToList();
        references.AddRange([value.Consumer.Installed.Footprint, value.Consumer.Manifest, value.Provider.Installed.Footprint, value.Provider.Manifest]);
        if (value.Choice is { } choice) references.AddRange([choice.Snapshot, choice.Content]);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long resourceBytes = 0;
        foreach (var resource in value.ResourceTrees)
        {
            if (!names.Add(resource.Name) || !paths.Add(resource.Path)) throw Invalid();
            ProjectSubsetPolicy.Require(resource.Path, resource.IncludedPaths is { } declared ? declared : (IReadOnlyList<string>?)null);
            var archive = ReadArchive(blobs, resource.Archive);
            if (resource.IncludedPaths is { } subset) ProjectSubsetPolicy.RequireClosure(archive, subset);
            resourceBytes = checked(resourceBytes + archive.Snapshot.Entries.Sum(item => item.SizeBytes));
            if (resourceBytes > 1_073_741_824) throw Invalid(); references.AddRange(archive.Contents);
        }
        var configurations = new ConfigurationSnapshotCodec(blobs);
        foreach (var mount in value.Configurations)
        {
            PhysicalPathPolicy.ValidateRelativePath(mount.Configuration.Path);
            references.AddRange(configurations.Read(mount.Snapshot).Entries.Where(item => item.Kind == FileObjectKind.File)
                .Select(item => new BlobReference($"blobs/content/{item.Sha256}.bin", item.SizeBytes, item.Sha256!)));
        }
        foreach (var item in value.SourceFiles.Select(item => item.Content).Concat(value.ConfigurationFiles.Select(item => item.Content)))
        { Content(item); if (item.SizeBytes > 8 * 1024 * 1024) throw Invalid(); }
        if (value.SourceFiles.Sum(item => item.Content.SizeBytes) + value.ConfigurationFiles.Sum(item => item.Content.SizeBytes) > 32 * 1024 * 1024)
            throw Invalid();
        return references.Distinct().OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToImmutableArray();
    }

    internal static ArchivedFileTree ReadArchive(ImmutableBlobStore blobs, BlobReference reference)
    {
        var archive = ReadDocument<ArchivedFileTree>(blobs, reference);
        FileTreeArchive.ValidateArchive(archive); return archive;
    }

    private static T ReadDocument<T>(ImmutableBlobStore blobs, BlobReference reference)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw Invalid();
        using var input = blobs.OpenVerified(reference); var bytes = ReadBytes(input);
        using var parsed = StrictJson.Parse(bytes);
        var result = parsed.Deserialize<T>(Options) ?? throw Invalid();
        if (!BoundedJson.Serialize(result).AsSpan().SequenceEqual(bytes)) throw Invalid();
        return result;
    }

    private static byte[] ReadBytes(FileStream input)
    { var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes; }
    private static void Content(BlobReference reference)
    {
        if (reference.RelativePath != $"blobs/content/{reference.Sha256}.bin" || reference.SizeBytes < 1 || !FileTreePolicy.Digest(reference.Sha256))
            throw Invalid();
    }
    private static IOException Invalid() => new("compiled_project_invalid");
}
