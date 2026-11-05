using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Maintenance;
using UpdateRecipe = NexusPipeline.Platform.Storage.Maintenance.UpdateRecipe;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class ApplicationPayloadSeed
{
    internal static void Prepare(string root)
    {
        Directory.CreateDirectory(root);
        WriteMissing(ApplicationTree.HostEntry, [11, 17, 23]);
        WriteMissing(ApplicationTree.MaintenanceEntry, [31, 37, 41]);
        WriteMissing("README.md", "Controlled application fixture"u8.ToArray());
        WriteMissing(ApplicationTree.DesktopEntry, [43, 47]);
        WriteMissing(ApplicationTree.DesktopArchive, [53, 59]);
        WriteMissing("resources/desktop/locales/en-US.pak", [61, 67]);
        WriteMissing("resources/desktop/locales/zh-CN.pak", [71, 73]);
        void WriteMissing(string name, byte[] bytes)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }

    internal static byte[] Write(string root, string version = "0.18.0")
    {
        Prepare(root);
        var entries = ApplicationTree.Capture(new(SnapshotBudget.Default), root, default).Entries
            .Where(entry => entry.Kind == NexusPipeline.Core.Configuration.FileObjectKind.File &&
                entry.RelativePath != ApplicationTree.ManifestPath).ToArray();
        var files = entries.Select(entry => new { path = entry.RelativePath, sizeBytes = entry.SizeBytes,
            sha256 = entry.Sha256!, kind = ApplicationManifest.Kind(entry.RelativePath) }).ToArray();
        var runtime = new
        {
            schemaVersion = 2, electronVersion = "44.9.0", electronArchiveSha256 = new string('a', 64),
            runtimeProfileId = "win-x64-en-zh-v1", runtimeProfileSha256 = new string('b', 64),
            files = files.Where(file => file.path.StartsWith("resources/desktop/", StringComparison.Ordinal) && file.kind != "desktop-app")
                .Select(file => new { path = file.path[18..], file.sizeBytes, file.sha256 }).OrderBy(file => file.path, StringComparer.Ordinal)
        };
        var bytes = Canonical(new
        {
            schemaVersion = 2, product = "NexusPipeline", productVersion = version, installationGeneration = "g0180", rid = "win-x64",
            sourceSha = new string('c', 40), sourceTreeSha = new string('d', 40), buildId = new string('e', 64), frontendHash = new string('f', 64),
            desktop = new { entryPoint = ApplicationTree.DesktopEntry, appArchive = ApplicationTree.DesktopArchive,
                runtime.electronVersion, runtime.electronArchiveSha256, packageLockSha256 = new string('a', 64), bootstrapProtocolVersion = 1,
                runtime.runtimeProfileId, runtime.runtimeProfileSha256, runtimeInventorySha256 = Digest(Canonical(runtime)) }, files
        });
        File.WriteAllBytes(Path.Combine(root, ApplicationTree.ManifestPath), bytes);
        return bytes;
    }

    internal static byte[] Recipe(byte[] manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        var included = root.GetProperty("files").EnumerateArray().Select(item => new { path = item.GetProperty("path").GetString()!,
            sizeBytes = item.GetProperty("sizeBytes").GetInt64(), sha256 = item.GetProperty("sha256").GetString()! }).ToList();
        included.Add(new { path = ApplicationTree.ManifestPath, sizeBytes = (long)manifest.Length, sha256 = Digest(manifest) });
        return Canonical(new
        {
            schemaVersion = 1, packageKind = "update", product = "NexusPipeline", rid = "win-x64", installationGeneration = "g0180",
            target = new { version = root.GetProperty("productVersion").GetString(), buildId = root.GetProperty("buildId").GetString(),
                frontendHash = root.GetProperty("frontendHash").GetString(), payloadManifestSha256 = Digest(manifest) }, @base = (object?)null,
            requirements = new { readerProtocol = 2, requiredCapabilities = new[] {
                "payload-manifest-v2", "single-delta-v1", "transaction-recovery-v1" }, routeId = "g0180-no-store-change-v1" },
            includedFiles = included.OrderBy(file => file.path, StringComparer.Ordinal), reusedFiles = Array.Empty<object>(),
            removedFiles = Array.Empty<object>(), bundledFiles = Array.Empty<object>(),
            manualCover = new { allowed = false, minimumSourceVersion = "0.18.0", exactBaseRequired = false,
                requiresPostStartReconciliation = false, reason = "Controlled Host transaction fixture" }
        });
    }

    internal static void Archive(string candidate, string destination, byte[] manifest)
    {
        var files = ApplicationTree.Capture(new(SnapshotBudget.Default), candidate, default).Entries
            .Where(entry => entry.Kind == NexusPipeline.Core.Configuration.FileObjectKind.File);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var target = zip.CreateEntry(file.RelativePath, CompressionLevel.Fastest).Open();
            using var input = File.OpenRead(Path.Combine(candidate, file.RelativePath)); input.CopyTo(target);
        }
        Write(UpdateRecipe.Path, Recipe(manifest));
        foreach (var name in UpdateRecipe.Metadata.Skip(1)) Write(name, "Controlled application update guide"u8.ToArray());
        void Write(string name, byte[] bytes) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
    }

    private static byte[] Canonical(object value)
    {
        JsonNode? Sort(JsonNode? node) => node switch
        {
            JsonObject map => new JsonObject(map.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new KeyValuePair<string, JsonNode?>(item.Key, Sort(item.Value)))),
            JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
            _ => node?.DeepClone()
        };
        return JsonSerializer.SerializeToUtf8Bytes(Sort(JsonSerializer.SerializeToNode(value)));
    }

    internal static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
