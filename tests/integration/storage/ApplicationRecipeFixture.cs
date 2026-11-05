using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Archives;
using NexusPipeline.Platform.Storage.Maintenance;
using UpdateRecipe = NexusPipeline.Platform.Storage.Maintenance.UpdateRecipe;
using UpdatePayloadManifest = NexusPipeline.Platform.Storage.Maintenance.UpdatePayloadManifest;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ApplicationRecipeFixture : IDisposable
{
    internal ContractReader Contracts { get; } = new();
    internal string Original { get; }
    internal string Candidate { get; }
    internal byte[] Manifest { get; }
    internal ArchivedFileTree Before { get; }
    internal ImmutableBlobStore Blobs { get; }
    internal UpdatePayloadManifest Payload { get; }
    internal HostUpdateFeed Feed { get; }
    internal JsonNode Recipe { get; }
    private readonly List<MemoryStream> _streams = [];

    internal ApplicationRecipeFixture(bool delta = false)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "recipe-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(Contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        Blobs = new(instance, Contracts);
        Original = Path.Combine(root, "original"); Candidate = Path.Combine(root, "candidate");
        ApplicationPayloadSeed.Prepare(Original); ApplicationPayloadSeed.Prepare(Candidate);
        File.WriteAllText(Path.Combine(Original, "README.md"), "original");
        File.WriteAllText(Path.Combine(Candidate, "README.md"), "candidate");
        File.WriteAllBytes(Path.Combine(Original, "resources/desktop/obsolete.bin"), [3, 5]);
        File.WriteAllBytes(Path.Combine(Candidate, "resources/desktop/new.bin"), [7, 11]);
        var originalManifest = ApplicationPayloadSeed.Write(Original);
        Manifest = ApplicationPayloadSeed.Write(Candidate, "0.18.1");
        Before = new FileTreeArchive(new(SnapshotBudget.Default), Blobs).CaptureApplication(Original, default);
        var binding = new UpdateStoreBinding(ExtensionStoreChannel.Stable, Guid.NewGuid(), 1,
            "https://store.example.test/catalog.json", new('a', 64), "https://store.example.test/catalog.sig.json");
        HostUpdateRelease Release(string version, byte[] manifest, long id) => new(version, "0.18.0",
            new("https://assets.example.test/payload.zip", 1024, new('b', 64)), ApplicationPayloadSeed.Digest(manifest),
            DateTimeOffset.UtcNow, binding, id, id + 1);
        var basis = Release("0.18.0", originalManifest, 1); var target = Release("0.18.1", Manifest, 3);
        Feed = new(new(HostUpdateChannel.Stable, 1, new('c', 64)), [basis, target]);
        Payload = new UpdateManifestCodec(Contracts).Read(Manifest, target, requiredProfile: "test");
        Recipe = JsonNode.Parse(ApplicationPayloadSeed.Recipe(Manifest))!;
        if (delta) SetDelta(basis);
    }

    private void SetDelta(HostUpdateRelease basis)
    {
        var included = Recipe["includedFiles"]!.AsArray(); var reused = Recipe["reusedFiles"]!.AsArray();
        foreach (var file in included.ToArray())
        {
            var path = file!["path"]!.GetValue<string>();
            if (path == ApplicationTree.ManifestPath) continue;
            if (Before.Snapshot.Entries.Any(entry => entry.RelativePath == path && entry.Kind == FileObjectKind.File &&
                entry.SizeBytes == file["sizeBytes"]!.GetValue<long>() && entry.Sha256 == file["sha256"]!.GetValue<string>()))
            { included.Remove(file); reused.Add(file); }
        }
        var target = Payload.Files.Select(file => file.RelativePath).Append(ApplicationTree.ManifestPath).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Before.Snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File && !target.Contains(entry.RelativePath)))
            Recipe["removedFiles"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { path = file.RelativePath, file.SizeBytes, file.Sha256 },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Recipe["base"] = JsonSerializer.SerializeToNode(new { releaseTag = "v0.18.0", releaseId = basis.ReleaseId,
            assetId = basis.AssetId, assetSha256 = basis.Artifact.Sha256, payloadManifestSha256 = basis.PayloadManifestSha256 });
        Recipe["manualCover"]!["exactBaseRequired"] = true;
    }

    internal PayloadArchive Open(JsonNode? recipe = null, bool unknownMetadata = false)
    {
        recipe ??= Recipe;
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in recipe["includedFiles"]!.AsArray())
            {
                var name = file!["path"]!.GetValue<string>();
                Write(name, File.ReadAllBytes(Path.Combine(Candidate, name)));
            }
            Write(UpdateRecipe.Path, JsonSerializer.SerializeToUtf8Bytes(recipe));
            foreach (var name in UpdateRecipe.Metadata.Skip(1)) Write(name, "Controlled update guide"u8.ToArray());
            if (unknownMetadata) Write("package-info/unknown.txt", [1]);
            void Write(string name, byte[] bytes) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
        }
        var input = new MemoryStream(output.ToArray()); _streams.Add(input);
        return PayloadArchive.Open(input, new(4096, 536870912), UpdateRecipe.Invalid, PayloadArchiveKind.Application);
    }

    internal UpdateRecipe Read(PayloadArchive archive) => UpdateRecipe.Read(archive, Payload, Contracts);
    public void Dispose() { foreach (var stream in _streams) stream.Dispose(); }
}
