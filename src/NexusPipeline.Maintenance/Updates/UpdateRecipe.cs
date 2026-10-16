using System.Text.Json;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateRecipeBase(string ReleaseTag, long ReleaseId, long AssetId,
    string AssetSha256, string PayloadManifestSha256);

internal sealed record UpdateRecipe(string PackageKind, string Version, string BuildId, string FrontendHash,
    string PayloadManifestSha256, UpdateRecipeBase? Base, IReadOnlyList<UpdateTargetFile> IncludedFiles,
    IReadOnlyList<UpdateTargetFile> ReusedFiles, IReadOnlyList<UpdateTargetFile> RemovedFiles,
    IReadOnlyList<UpdateTargetFile> BundledFiles, bool ManualCoverAllowed)
{
    internal const string RecipePath = "package-info/recipe.json";
    internal const string UpdateEntry = ApplicationPayload.UpdateEntry;
    internal const int MaxBytes = 2 * 1024 * 1024;
    internal static readonly string[] Guides = ["package-info/README.zh-CN.md", "package-info/README.en-US.md"];
    internal static readonly string[] Capabilities = ["payload-manifest-v2", "single-delta-v1", "transaction-recovery-v1"];

    internal static UpdateRecipe Parse(ReadOnlyMemory<byte> bytes, string version, string kind)
    {
        try { return ParseCore(bytes, version, kind); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException
            or OverflowException or ArgumentException or KeyNotFoundException)
        { throw new InvalidDataException("update.recipe_invalid", error); }
    }

    private static UpdateRecipe ParseCore(ReadOnlyMemory<byte> bytes, string version, string kind)
    {
        Require(bytes.Length is > 0 and <= MaxBytes, "size");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        Fields(root, "schemaVersion", "packageKind", "product", "rid", "installationGeneration", "target", "base",
            "requirements", "includedFiles", "reusedFiles", "removedFiles", "bundledFiles", "manualCover");
        Require(root.GetProperty("schemaVersion").GetInt32() == 1 && Text(root, "product") == "NexusPipeline"
            && Text(root, "rid") == "win-x64" && Text(root, "installationGeneration") == InstallationGeneration.Id
            && kind is "portable" or "update" && Text(root, "packageKind") == kind, "identity");
        var target = root.GetProperty("target");
        Fields(target, "version", "buildId", "frontendHash", "payloadManifestSha256");
        Require(Text(target, "version") == version && UpdateCatalog.TryParseTag("v" + version, out _), "version");
        string build = Hash(target, "buildId"), frontend = Hash(target, "frontendHash"), manifest = Hash(target, "payloadManifestSha256");
        var requirements = root.GetProperty("requirements");
        Fields(requirements, "readerProtocol", "requiredCapabilities", "routeId");
        Require(requirements.GetProperty("readerProtocol").GetInt32() == 2
            && Text(requirements, "routeId") == UpdateMigrationRegistry.NoStoreChangeRoute
            && requirements.GetProperty("requiredCapabilities").EnumerateArray().Select(item => item.GetString()).SequenceEqual(Capabilities), "requirements");
        UpdateRecipeBase? baseline = null;
        var source = root.GetProperty("base");
        if (source.ValueKind != JsonValueKind.Null)
        {
            Fields(source, "releaseTag", "releaseId", "assetId", "assetSha256", "payloadManifestSha256");
            string tag = Text(source, "releaseTag");
            long releaseId = source.GetProperty("releaseId").GetInt64(), assetId = source.GetProperty("assetId").GetInt64();
            Require(UpdateCatalog.TryParseTag(tag, out var parsed) && tag == "v" + parsed && releaseId > 0 && assetId > 0, "base");
            UpdateCatalog.TryParseTag("v0.17.3", out var minimum);
            UpdateCatalog.TryParseTag("v" + version, out var destination);
            Require(parsed.CompareTo(minimum) >= 0 && parsed.CompareTo(destination) < 0, "base_version");
            baseline = new(tag, releaseId, assetId, Hash(source, "assetSha256"), Hash(source, "payloadManifestSha256"));
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var included = ReadFiles(root.GetProperty("includedFiles"), "included", names);
        var reused = ReadFiles(root.GetProperty("reusedFiles"), "reused", names);
        var removed = ReadFiles(root.GetProperty("removedFiles"), "removed", names);
        var bundled = ReadFiles(root.GetProperty("bundledFiles"), "bundled", names);
        Require(bundled.Count == 0 || kind == "portable", "bundled_kind");
        var files = included.Concat(reused).ToArray();
        Require(names.Count <= UpdatePackage.MaxArchiveEntries && files.Concat(bundled).Sum(file => file.SizeBytes) <= UpdatePackage.MaxExtractedBytes, "budget");
        var targetNames = files.Concat(bundled).Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in targetNames)
            Require(!name.Split('/').Select((_, index) => index).Skip(1)
                .Any(index => targetNames.Contains(string.Join('/', name.Split('/').Take(index)))), "file_directory_conflict");
        Require(included.Any(file => file.Path == ApplicationPayload.ManifestPath && file.Sha256 == manifest), "manifest");
        Require(new[] { "NexusPipeline.exe", UpdateEntry, "README.md", ApplicationPayload.DesktopEntry, ApplicationPayload.DesktopArchive,
            "resources/desktop/resources/app.asar.unpacked/node_modules/@koromix/koffi-win32-x64/win32_x64/koffi.node" }
            .All(name => files.Any(file => file.Path == name && file.SizeBytes > 0)), "required_files");
        Require(baseline is not null || reused.Count == 0 && removed.Count == 0, "missing_base");
        Require(kind != "portable" || baseline is null && reused.Count == 0 && removed.Count == 0, "portable");
        var manual = root.GetProperty("manualCover");
        Fields(manual, "allowed", "minimumSourceVersion", "exactBaseRequired", "requiresPostStartReconciliation", "reason");
        bool allowed = manual.GetProperty("allowed").GetBoolean(), exact = manual.GetProperty("exactBaseRequired").GetBoolean(),
            reconcile = manual.GetProperty("requiresPostStartReconciliation").GetBoolean();
        Require(Text(manual, "minimumSourceVersion") == "0.17.3" && Text(manual, "reason").Length is > 0 and <= 256
            && (!allowed || kind == "update" && baseline is not null && removed.Count == 0 && exact && reconcile), "manual_cover");
        return new(kind, version, build, frontend, manifest, baseline, included, reused, removed, bundled, allowed);
    }

    private static List<UpdateTargetFile> ReadFiles(JsonElement array, string group, HashSet<string> names)
    {
        Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= UpdatePackage.MaxArchiveEntries, "file_count");
        var files = new List<UpdateTargetFile>();
        long total = 0;
        foreach (var item in array.EnumerateArray())
        {
            Fields(item, "path", "sizeBytes", "sha256");
            string path = Text(item, "path"), hash = Hash(item, "sha256");
            long size = item.GetProperty("sizeBytes").GetInt64();
            bool allowedPath = group == "bundled"
                ? path.StartsWith("plugins/EmulatorSupport/", StringComparison.Ordinal) || path.StartsWith("plugins/LiveScreenshot/", StringComparison.Ordinal)
                : path is "NexusPipeline.exe" or UpdateEntry or "README.md" or ApplicationPayload.ManifestPath || path.StartsWith("resources/desktop/", StringComparison.Ordinal);
            Require(ApplicationPayload.SafePath(path) && allowedPath && names.Add(path)
                && size is >= 0 and <= UpdatePackage.MaxSingleEntryBytes, "file");
            total = checked(total + size);
            Require(total <= UpdatePackage.MaxExtractedBytes, "file_budget");
            files.Add(new(path, size, hash, group));
        }
        return files;
    }

    private static string Text(JsonElement node, string field) => node.GetProperty(field).GetString()
        ?? throw new InvalidDataException("update.recipe_null");
    private static string Hash(JsonElement node, string field)
    {
        string hash = Text(node, field);
        Require(UpdateInventory.Hash(hash), "hash");
        return hash;
    }
    private static void Fields(JsonElement node, params string[] names) => Require(node.ValueKind == JsonValueKind.Object
        && node.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)), "fields");
    private static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidDataException("update.recipe_" + code);
    }
}
