using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record ApplicationManifest(string HostVersion, string? SourceSha, string? SourceTreeSha,
    string BuildId, string FrontendHash, ImmutableArray<UpdatePayloadFile> Files)
{
    internal static ApplicationManifest Read(ContractReader contracts, ReadOnlyMemory<byte> original, string? profile)
    {
        if (original.Length is < 1 or > StrictJson.MaximumBytes || original.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ||
            profile is not (null or "production" or "test")) throw Invalid();
        using var document = contracts.Read(original, "update-payload-manifest.schema.json");
        var root = document.RootElement;
        var source = root.GetProperty("sourceSha").GetString(); var tree = root.GetProperty("sourceTreeSha").GetString();
        if ((source is null) != (tree is null) || profile == "production" && source is null) throw Invalid();
        var files = root.GetProperty("files").EnumerateArray().Select(item => new UpdatePayloadFile(
            item.GetProperty("path").GetString()!, item.GetProperty("sizeBytes").GetInt64(),
            item.GetProperty("sha256").GetString()!, item.GetProperty("kind").GetString()!)).ToImmutableArray();
        if (files.IsEmpty || files.Length > 4095 || files.Sum(file => file.SizeBytes) > 536870912 ||
            files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw Invalid();
        foreach (var file in files)
        {
            PhysicalPathPolicy.ValidateRelativePath(file.RelativePath);
            if (file.Kind != Kind(file.RelativePath)) throw Invalid();
        }
        foreach (var required in new[] { ApplicationTree.HostEntry, ApplicationTree.MaintenanceEntry, "README.md",
            ApplicationTree.DesktopEntry, ApplicationTree.DesktopArchive })
            if (!files.Any(file => file.RelativePath == required && file.SizeBytes > 0)) throw Invalid();
        if (files.Single(file => file.RelativePath == ApplicationTree.HostEntry).Sha256 ==
            files.Single(file => file.RelativePath == ApplicationTree.MaintenanceEntry).Sha256) throw Invalid();
        RequireRuntime(root.GetProperty("desktop"), files);
        return new(root.GetProperty("productVersion").GetString()!, source, tree,
            root.GetProperty("buildId").GetString()!, root.GetProperty("frontendHash").GetString()!, files);
    }

    internal static string Kind(string path) => path switch
    {
        ApplicationTree.HostEntry => "host",
        ApplicationTree.MaintenanceEntry => "update-entry",
        "README.md" => "readme",
        ApplicationTree.DesktopEntry => "desktop-entry",
        ApplicationTree.DesktopArchive => "desktop-app",
        "resources/desktop/LICENSE" or "resources/desktop/LICENSES.chromium.html" => "license",
        "resources/desktop/version" => "runtime-manifest",
        _ when path.StartsWith("resources/desktop/", StringComparison.Ordinal) => "desktop-runtime",
        _ => throw Invalid()
    };

    private static void RequireRuntime(JsonElement desktop, ImmutableArray<UpdatePayloadFile> files)
    {
        var locales = files.Where(file => file.RelativePath.StartsWith("resources/desktop/locales/", StringComparison.Ordinal))
            .Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray();
        if (!locales.SequenceEqual(new[] { "resources/desktop/locales/en-US.pak", "resources/desktop/locales/zh-CN.pak" })) throw Invalid();
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("electronArchiveSha256", desktop.GetProperty("electronArchiveSha256").GetString());
            writer.WriteString("electronVersion", desktop.GetProperty("electronVersion").GetString());
            writer.WritePropertyName("files"); writer.WriteStartArray();
            foreach (var file in files.Where(file => file.RelativePath.StartsWith("resources/desktop/", StringComparison.Ordinal) &&
                file.Kind != "desktop-app").OrderBy(file => file.RelativePath, StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("path", file.RelativePath[18..]);
                writer.WriteString("sha256", file.Sha256); writer.WriteNumber("sizeBytes", file.SizeBytes); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("runtimeProfileId", desktop.GetProperty("runtimeProfileId").GetString());
            writer.WriteString("runtimeProfileSha256", desktop.GetProperty("runtimeProfileSha256").GetString());
            writer.WriteNumber("schemaVersion", 2); writer.WriteEndObject();
        }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())) != desktop.GetProperty("runtimeInventorySha256").GetString())
            throw Invalid();
    }

    internal static void RequireTree(ArchivedFileTree tree, ApplicationManifest manifest, BlobReference original, bool allowMissing)
    {
        FileTreeArchive.ValidateArchive(tree);
        RequireSnapshot(tree.Snapshot, manifest, original.SizeBytes, original.Sha256, allowMissing);
    }

    internal static void RequireSnapshot(FileTreeSnapshot snapshot, ApplicationManifest manifest, long manifestBytes,
        string manifestSha256, bool allowMissing)
    {
        ApplicationTree.Validate(snapshot, complete: !allowMissing);
        var expected = manifest.Files.ToDictionary(file => file.RelativePath, file => (file.SizeBytes, file.Sha256), StringComparer.Ordinal);
        expected.Add(ApplicationTree.ManifestPath, (manifestBytes, manifestSha256));
        var files = snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File).ToArray();
        foreach (var file in files)
            if (!expected.TryGetValue(file.RelativePath, out var content) || content != (file.SizeBytes, file.Sha256)) throw Invalid();
        foreach (var name in expected.Keys)
            if (!files.Any(file => file.RelativePath == name) &&
                !(allowMissing && name is "README.md" or ApplicationTree.MaintenanceEntry)) throw Invalid();
        var directories = files.SelectMany(file => Ancestors(file.RelativePath)).Append("").ToHashSet(StringComparer.Ordinal);
        if (snapshot.Entries.Count != files.Length + directories.Count ||
            snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.Directory).Any(entry => !directories.Contains(entry.RelativePath)))
            throw Invalid();
    }

    internal static IEnumerable<string> Ancestors(string path)
    {
        for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1)) yield return path[..index];
    }

    internal static IOException Invalid() => new("update_payload_manifest_unbound");
}
