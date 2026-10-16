using System.Text.Json;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateTargetFile(string Path, long SizeBytes, string Sha256, string ComponentId);
internal sealed record UpdateTargetManifest(string Version, string BuildId, string FrontendHash, string PayloadManifestSha256,
    IReadOnlyDictionary<string, UpdateReleaseAsset> Components, IReadOnlyList<UpdateTargetFile> Files)
{
    internal const int MaxBytes = 2 * 1024 * 1024;
    internal static readonly string[] ComponentIds = ["host-core", "desktop-app", "desktop-runtime"];
    internal static readonly string[] Capabilities = ["payload-manifest-v1", "migration-snapshot-v1", "transaction-recovery-v1"];

    internal static UpdateTargetManifest Parse(ReadOnlyMemory<byte> bytes, ReleaseInfo release)
    {
        try { return ParseCore(bytes, release); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw new InvalidDataException("update.target_invalid", error); }
    }

    private static UpdateTargetManifest ParseCore(ReadOnlyMemory<byte> bytes, ReleaseInfo release)
    {
        if (bytes.Length is < 1 or > MaxBytes) throw new InvalidDataException("update.target_size");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        Fields(root, "schemaVersion", "product", "target", "requirements", "fullPackage", "components", "files", "migration");
        Require(root.GetProperty("schemaVersion").GetInt32() == 1 && Text(root, "product") == "NexusPipeline", "protocol");
        var target = root.GetProperty("target");
        Fields(target, "version", "rid", "installationGeneration", "buildId", "frontendHash", "payloadManifestSha256");
        Require(Text(target, "version") == release.VersionText && Text(target, "rid") == "win-x64"
            && Text(target, "installationGeneration") == InstallationGeneration.Id, "identity");
        string build = Hash(target, "buildId"), frontend = Hash(target, "frontendHash"), manifest = Hash(target, "payloadManifestSha256");
        var requirements = root.GetProperty("requirements");
        Fields(requirements, "readerProtocol", "runnerHandshakeProtocol", "requiredCapabilities");
        Require(requirements.GetProperty("readerProtocol").GetInt32() == 1
            && requirements.GetProperty("runnerHandshakeProtocol").GetInt32() == 1, "capability");
        string[] capabilities = requirements.GetProperty("requiredCapabilities").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Require(capabilities.Order(StringComparer.Ordinal).SequenceEqual(Capabilities.Order(StringComparer.Ordinal)), "capability");
        var full = root.GetProperty("fullPackage");
        Fields(full, "assetName");
        Require(Text(full, "assetName") == release.ZipAsset.Name, "full_asset");
        var components = new Dictionary<string, UpdateReleaseAsset>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("components").EnumerateArray())
        {
            Fields(item, "id", "assetName");
            string id = Text(item, "id"), name = Text(item, "assetName");
            Require(ComponentIds.Contains(id) && name == $"NexusPipeline-v{release.VersionText}-win-x64-{id}.zip", "component");
            Require(release.Assets?.TryGetValue(name, out _) == true, "component_asset");
            Require(components.TryAdd(id, release.Assets![name]), "component_duplicate");
        }
        Require(components.Count == ComponentIds.Length, "component_set");
        var files = new List<UpdateTargetFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            Fields(item, "path", "sizeBytes", "sha256", "componentId");
            string name = Text(item, "path"), hash = Hash(item, "sha256"), component = Text(item, "componentId");
            long size = item.GetProperty("sizeBytes").GetInt64();
            Require(ApplicationPayload.SafePath(name) && names.Add(name) && components.ContainsKey(component)
                && size is >= 0 and <= UpdatePackage.MaxSingleEntryBytes, "file");
            bool core = name is "NexusPipeline.exe" or "README.md" or ApplicationPayload.ManifestPath;
            Require(core ? component == "host-core" : name.StartsWith("resources/desktop/", StringComparison.Ordinal) && component != "host-core", "file_owner");
            files.Add(new(name, size, hash, component));
            total = checked(total + size);
            Require(files.Count <= UpdatePackage.MaxArchiveEntries && total <= UpdatePackage.MaxExtractedBytes, "file_limits");
        }
        Require(ComponentIds.All(id => files.Any(file => file.ComponentId == id)), "empty_component");
        foreach (string name in names)
            Require(!name.Split('/').Select((_, index) => index).Skip(1).Any(index => names.Contains(string.Join('/', name.Split('/').Take(index)))), "file_directory_conflict");
        Require(new[] { "NexusPipeline.exe", "README.md", ApplicationPayload.ManifestPath, ApplicationPayload.DesktopEntry, ApplicationPayload.DesktopArchive }
            .All(name => files.Any(file => file.Path == name && file.SizeBytes > 0)), "required_file");
        Require(files.Single(file => file.Path == ApplicationPayload.ManifestPath).Sha256 == manifest, "payload_manifest");
        var migration = root.GetProperty("migration");
        Fields(migration, "routeId", "sourceGeneration", "targetGeneration", "stores", "migratorIds", "runner");
        Require(Text(migration, "routeId") == UpdateMigrationRegistry.NoStoreChangeRoute
            && Text(migration, "sourceGeneration") == InstallationGeneration.Id && Text(migration, "targetGeneration") == InstallationGeneration.Id
            && migration.GetProperty("stores").GetArrayLength() == 0 && migration.GetProperty("migratorIds").GetArrayLength() == 0, "migration_route");
        var runner = migration.GetProperty("runner");
        Fields(runner, "kind", "entryPath");
        Require(Text(runner, "kind") == "target-host" && Text(runner, "entryPath") == "NexusPipeline.exe", "runner");
        return new(release.VersionText, build, frontend, manifest, components, files);
    }

    private static string Text(JsonElement value, string field) => value.GetProperty(field).GetString()
        ?? throw new InvalidDataException("update.target_null");
    private static string Hash(JsonElement value, string field)
    {
        string hash = Text(value, field);
        Require(UpdateInventory.Hash(hash), "hash");
        return hash;
    }
    private static void Fields(JsonElement value, params string[] names) => Require(value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)), "fields");
    private static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidDataException("update.target_" + code);
    }
}
