using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed record ApplicationPayloadFile(string Path, long SizeBytes, string Sha256, string Kind);
internal sealed record UpdatePayloadIdentity(string ManifestSha256, string BuildId, string FrontendHash, string Generation);

internal static class ApplicationPayload
{
    internal const string ManifestPath = "resources/payload-manifest.json";
    internal const string DesktopEntry = "resources/desktop/NexusPipeline.Desktop.exe";
    internal const string DesktopArchive = "resources/desktop/resources/app.asar";

    internal static ApplicationBuildIdentity Validate(string root, string? expectedVersion = null, bool installed = false)
    {
        PayloadPathSafety.RequireLinkFree(root);
        byte[] manifestBytes = ReadBounded(Path.Combine(root, ManifestPath), 2 * 1024 * 1024);
        using var document = JsonDocument.Parse(manifestBytes);
        JsonElement manifest = document.RootElement;
        Fields(manifest, ["schemaVersion", "product", "productVersion", "installationGeneration", "rid", "sourceSha", "sourceTreeSha", "buildId", "frontendHash", "desktop", "files"]);
        byte[] hostBytes = BundleResourceReader.Read(Path.Combine(root, "NexusPipeline.exe"), "NexusPipeline.dll", "NexusPipeline.Desktop.BuildIdentity", ApplicationBuildIdentity.MaxBytes);
        ApplicationBuildIdentity identity = ApplicationBuildIdentity.Parse(hostBytes);
        if (identity.InstallationGeneration != InstallationGeneration.Id || expectedVersion is not null && identity.ProductVersion != expectedVersion)
            throw new InvalidDataException("Application version or generation mismatch");
        byte[] desktopBytes = ReadAsarIdentity(Path.Combine(root, DesktopArchive));
        if (!hostBytes.AsSpan().SequenceEqual(desktopBytes)) throw new InvalidDataException("Host and desktop build identity mismatch");
        byte[] index = BundleResourceReader.Read(Path.Combine(root, "NexusPipeline.exe"), "NexusPipeline.dll", "NexusPipeline.Frontend.Index", 2 * 1024 * 1024);
        using var indexDocument = JsonDocument.Parse(index);
        if (indexDocument.RootElement.GetProperty("frontendHash").GetString() != identity.FrontendHash)
            throw new InvalidDataException("Embedded frontend identity mismatch");
        var indexes = indexDocument.RootElement.GetProperty("files").EnumerateArray().Where(item => item.GetProperty("path").GetString() == "index.html").ToArray();
        if (indexes.Length != 1) throw new InvalidDataException("Embedded index.html missing or duplicated");
        byte[] rawIndex = BundleResourceReader.Read(Path.Combine(root, "NexusPipeline.exe"), "NexusPipeline.dll", indexes[0].GetProperty("resourceName").GetString()!, 64 * 1024 * 1024);
        if (Convert.ToHexStringLower(SHA256.HashData(rawIndex)) != identity.FrontendHash) throw new InvalidDataException("Raw frontend hash mismatch");
        foreach (var item in new Dictionary<string, string>
        {
            ["product"] = "NexusPipeline", ["rid"] = "win-x64", ["productVersion"] = identity.ProductVersion,
            ["installationGeneration"] = identity.InstallationGeneration, ["buildId"] = identity.BuildId, ["frontendHash"] = identity.FrontendHash,
            ["sourceSha"] = identity.BuildInputs.GetProperty("sourceSha").GetString()!, ["sourceTreeSha"] = identity.BuildInputs.GetProperty("sourceTreeSha").GetString()!,
        }) if (manifest.GetProperty(item.Key).GetString() != item.Value) throw new InvalidDataException("Payload identity mismatch: " + item.Key);
        if (manifest.GetProperty("schemaVersion").GetRawText() != "1") throw new InvalidDataException("Payload schema mismatch");
        JsonElement desktop = manifest.GetProperty("desktop");
        bool profiled = identity.BuildInputs.GetProperty("schemaVersion").GetRawText() == "2";
        string[] profileFields = ["runtimeProfileId", "runtimeProfileSha256", "runtimeInventorySha256"];
        Fields(desktop, ["entryPoint", "appArchive", "electronVersion", "electronArchiveSha256", "packageLockSha256", "bootstrapProtocolVersion", .. profiled ? profileFields : []]);
        if (profiled && profileFields.Any(field => desktop.GetProperty(field).GetString() != identity.BuildInputs.GetProperty(field).GetString()))
            throw new InvalidDataException("Desktop runtime profile mismatch");
        if (desktop.GetProperty("entryPoint").GetString() != DesktopEntry || desktop.GetProperty("appArchive").GetString() != DesktopArchive
            || desktop.GetProperty("bootstrapProtocolVersion").GetRawText() != "1"
            || desktop.GetProperty("electronVersion").GetString() != identity.BuildInputs.GetProperty("electronVersion").GetString()
            || desktop.GetProperty("electronArchiveSha256").GetString() != identity.BuildInputs.GetProperty("electronArchiveSha256").GetString()
            || desktop.GetProperty("packageLockSha256").GetString() != identity.BuildInputs.GetProperty("desktopPackageLockSha256").GetString())
            throw new InvalidDataException("Desktop manifest mismatch");
        var listed = new Dictionary<string, ApplicationPayloadFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (JsonElement file in manifest.GetProperty("files").EnumerateArray())
        {
            Fields(file, ["path", "sizeBytes", "sha256", "kind"]);
            string path = file.GetProperty("path").GetString()!, hash = file.GetProperty("sha256").GetString()!, kind = file.GetProperty("kind").GetString()!;
            long size = file.GetProperty("sizeBytes").GetInt64();
            if (!SafePath(path) || path == ManifestPath || !ValidKind(path, kind) || size is < 0 or > 256L * 1024 * 1024
                || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)) || !listed.TryAdd(path, new(path, size, hash, kind)))
                throw new InvalidDataException("Invalid payload file");
            total += size; if (listed.Count > 4096 || total > 512L * 1024 * 1024) throw new InvalidDataException("Payload exceeds limits");
        }
        foreach (string required in new[] { "NexusPipeline.exe", "README.md", DesktopEntry, DesktopArchive,
            "resources/desktop/resources/app.asar.unpacked/node_modules/@koromix/koffi-win32-x64/win32_x64/koffi.node" })
            if (!listed.TryGetValue(required, out var file) || file.SizeBytes == 0) throw new InvalidDataException("Required payload missing");
        byte[] runtimeBytes = BundleResourceReader.Read(Path.Combine(root, "NexusPipeline.exe"), "NexusPipeline.dll", "NexusPipeline.Desktop.RuntimeFiles", 2 * 1024 * 1024);
        using var runtimeDocument = JsonDocument.Parse(runtimeBytes);
        var runtime = runtimeDocument.RootElement;
        Fields(runtime, ["schemaVersion", "electronVersion", "electronArchiveSha256", "files", .. profiled ? new[] { "runtimeProfileId", "runtimeProfileSha256" } : []]);
        if (profiled && (runtime.GetProperty("runtimeProfileId").GetString() != desktop.GetProperty("runtimeProfileId").GetString()
            || runtime.GetProperty("runtimeProfileSha256").GetString() != desktop.GetProperty("runtimeProfileSha256").GetString()
            || Convert.ToHexStringLower(SHA256.HashData(runtimeBytes)) != desktop.GetProperty("runtimeInventorySha256").GetString()))
            throw new InvalidDataException("Embedded runtime profile mismatch");
        if (runtime.GetProperty("schemaVersion").GetRawText() != (profiled ? "2" : "1")
            || runtime.GetProperty("electronVersion").GetString() != desktop.GetProperty("electronVersion").GetString()
            || runtime.GetProperty("electronArchiveSha256").GetString() != desktop.GetProperty("electronArchiveSha256").GetString())
            throw new InvalidDataException("Runtime inventory identity mismatch");
        var runtimeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in runtime.GetProperty("files").EnumerateArray())
        {
            Fields(item, ["path", "sizeBytes", "sha256"]);
            string name = "resources/desktop/" + item.GetProperty("path").GetString();
            if (!runtimeNames.Add(name) || !listed.TryGetValue(name, out var file)
                || file.SizeBytes != item.GetProperty("sizeBytes").GetInt64() || file.Sha256 != item.GetProperty("sha256").GetString())
                throw new InvalidDataException("Runtime inventory bytes mismatch");
        }
        string[] nativeFiles = [DesktopArchive, "resources/desktop/resources/app.asar.unpacked/node_modules/@koromix/koffi-win32-x64/win32_x64/koffi.node", "resources/desktop/koffi.LICENSE.txt"];
        if (listed.Keys.Any(path => path.StartsWith("resources/desktop/", StringComparison.Ordinal) && !runtimeNames.Contains(path) && !nativeFiles.Contains(path)))
            throw new InvalidDataException("Undeclared desktop dependency");
        var actual = (installed ? EnumerateApplication(root) : Enumerate(root)).Where(file => file != ManifestPath && !file.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (actual.Length != listed.Count || actual.Any(path => !listed.ContainsKey(path))) throw new InvalidDataException("Payload file set mismatch");
        foreach (var file in listed.Values)
        {
            string path = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            using var stream = File.OpenRead(path);
            if (stream.Length != file.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(stream)) != file.Sha256) throw new InvalidDataException("Payload bytes mismatch: " + file.Path);
        }
        return identity;
    }

    internal static UpdatePayloadIdentity Freeze(string root, string? version = null, bool installed = false)
    {
        var identity = Validate(root, version, installed);
        return new(Convert.ToHexStringLower(SHA256.HashData(ReadBounded(Path.Combine(root, ManifestPath), 2 * 1024 * 1024))), identity.BuildId, identity.FrontendHash, identity.InstallationGeneration);
    }

    internal static void VerifyFrozen(string root, UpdatePayloadIdentity frozen, string? version = null, bool installed = false)
    {
        if (Freeze(root, version, installed) != frozen) throw new InvalidDataException("Frozen application identity changed");
    }

    internal static IReadOnlyList<ApplicationPayloadFile> Files(string root)
    {
        using var document = JsonDocument.Parse(ReadBounded(Path.Combine(root, ManifestPath), 2 * 1024 * 1024));
        return document.RootElement.GetProperty("files").EnumerateArray().Select(item => new ApplicationPayloadFile(
            item.GetProperty("path").GetString()!, item.GetProperty("sizeBytes").GetInt64(), item.GetProperty("sha256").GetString()!, item.GetProperty("kind").GetString()!)).ToArray();
    }

    private static IEnumerable<string> EnumerateApplication(string root)
    {
        foreach (string name in new[] { "NexusPipeline.exe", "README.md", "resources" })
        {
            string path = Path.Combine(root, name);
            PayloadPathSafety.RequireLinkFree(path);
            if (Directory.Exists(path)) foreach (var child in Enumerate(path)) yield return name + '/' + child;
            else if (File.Exists(path)) yield return name;
        }
    }

    internal static bool SafePath(string path) => path.Length is > 0 and <= 240 && !Path.IsPathRooted(path)
        && !path.Any(c => char.IsControl(c) || "\\:%?*\"<>|".Contains(c))
        && path.Split('/').All(part => part.Length > 0 && part is not "." and not ".." && !part.EndsWith('.') && !part.EndsWith(' ')
            && !System.Text.RegularExpressions.Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    private static bool ValidKind(string path, string kind) => kind switch
    {
        "host" => path == "NexusPipeline.exe", "readme" => path == "README.md", "desktop-entry" => path == DesktopEntry,
        "desktop-app" => path == DesktopArchive, "desktop-runtime" => path.StartsWith("resources/desktop/", StringComparison.Ordinal) && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && path != DesktopArchive,
        "runtime-manifest" => path == "resources/desktop/version",
        "license" => path is "resources/desktop/LICENSE" or "resources/desktop/LICENSES.chromium.html" or "resources/desktop/koffi.LICENSE.txt",
        _ => false,
    };
    private static IEnumerable<string> Enumerate(string root)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked payload file");
            if (Directory.Exists(path)) foreach (string child in Enumerate(path)) yield return Path.GetFileName(path) + '/' + child;
            else yield return Path.GetFileName(path);
        }
    }
    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = File.OpenRead(path); if (stream.Length is < 1 || stream.Length > limit) throw new InvalidDataException("Payload metadata exceeds limit");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static void Fields(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(names.Order())) throw new InvalidDataException("Unknown or duplicate payload fields");
    }
    internal static byte[] ReadAsarIdentity(string path)
    {
        using var stream = File.OpenRead(path); byte[] prefix = new byte[8]; stream.ReadExactly(prefix);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));
        if (BinaryPrimitives.ReadInt32LittleEndian(prefix) != 4 || headerSize is < 8 or > 16 * 1024 * 1024 || headerSize > stream.Length - 8) throw new InvalidDataException("Invalid ASAR header");
        byte[] header = new byte[headerSize]; stream.ReadExactly(header); int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (jsonLength is < 1 || jsonLength > headerSize - 8) throw new InvalidDataException("Invalid ASAR metadata");
        using var document = JsonDocument.Parse(header.AsMemory(8, jsonLength));
        JsonElement files = document.RootElement.GetProperty("files");
        var entries = files.EnumerateObject().Where(p => p.Name == "desktop-build.json").ToArray();
        if (entries.Length != 1) throw new InvalidDataException("ASAR identity missing or duplicated");
        JsonElement entry = entries[0].Value;
        int size = entry.GetProperty("size").GetInt32(); string offsetText = entry.GetProperty("offset").GetString()!;
        if (entry.TryGetProperty("unpacked", out _) || entry.TryGetProperty("link", out _) || size is < 1 or > ApplicationBuildIdentity.MaxBytes
            || !long.TryParse(offsetText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long offset)
            || offset < 0 || offset > stream.Length - 8 - headerSize - size) throw new InvalidDataException("Invalid ASAR identity entry");
        stream.Position = 8L + headerSize + offset; byte[] bytes = new byte[size]; stream.ReadExactly(bytes); return bytes;
    }
}
