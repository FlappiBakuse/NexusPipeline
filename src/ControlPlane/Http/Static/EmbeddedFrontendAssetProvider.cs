using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace NexusPipeline.ControlPlane.Http.Static;

internal sealed class EmbeddedFrontendAssetProvider : IFrontendAssetProvider
{
    private readonly IReadOnlyDictionary<string, FrontendAsset> _files;
    private readonly Func<string, Stream?> _open;
    public string FrontendHash { get; }

    internal static EmbeddedFrontendAssetProvider FromAssembly()
    {
        Assembly assembly = typeof(EmbeddedFrontendAssetProvider).Assembly;
        using Stream? index = assembly.GetManifestResourceStream("NexusPipeline.Frontend.Index");
        if (index is null)
        {
#if NEXUS_TEST_HOST
            return new EmbeddedFrontendAssetProvider("", Array.Empty<FrontendAsset>(), assembly.GetManifestResourceStream);
#else
            throw new InvalidDataException("Embedded frontend index is missing");
#endif
        }
        using JsonDocument document = JsonDocument.Parse(index);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Invalid frontend index version");
        FrontendAsset[] assets = root.GetProperty("files").EnumerateArray().Select(item => new FrontendAsset(
            item.GetProperty("path").GetString()!, item.GetProperty("resourceName").GetString()!,
            item.GetProperty("sizeBytes").GetInt64(), item.GetProperty("sha256").GetString()!,
            item.GetProperty("contentType").GetString()!, item.GetProperty("immutable").GetBoolean())).ToArray();
        return new(root.GetProperty("frontendHash").GetString()!, assets, assembly.GetManifestResourceStream);
    }

    internal EmbeddedFrontendAssetProvider(string frontendHash, IReadOnlyList<FrontendAsset> files, Func<string, Stream?> open)
    {
        if (files.Count > 5000) throw new InvalidDataException("Frontend index exceeds the file limit");
        var map = new Dictionary<string, FrontendAsset>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FrontendAsset file in files)
        {
            if (Normalize("/" + file.Path) != file.Path || file.SizeBytes is < 0 or > 64 * 1024 * 1024
                || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigitLower(c))
                || file.ResourceName != "NexusPipeline.Frontend.Asset." + file.Sha256
                || !names.Add(file.Path) || !map.TryAdd(file.Path, file)) throw new InvalidDataException("Invalid frontend asset index");
        }
        if (files.Count != 0 && (!map.TryGetValue("index.html", out FrontendAsset? index) || index.Sha256 != frontendHash))
            throw new InvalidDataException("Frontend hash does not match index.html");
        _files = map;
        _open = open;
        FrontendHash = frontendHash;
    }

    public FrontendAsset? Resolve(string requestPath)
    {
        string? path = Normalize(requestPath);
        if (path is null) return null;
        if (path.Length == 0) path = "index.html";
        if (_files.TryGetValue(path, out FrontendAsset? asset)) return asset;
        string first = path.Split('/')[0];
        if (first is "api" or "plugin-assets" or "resources" or "plugins" or "config" or "data" or "history" or "logs") return null;
        return !Path.HasExtension(path) && _files.TryGetValue("index.html", out FrontendAsset? entry) ? entry : null;
    }

    internal static string? Normalize(string path)
    {
        if (!path.StartsWith('/') || path.Contains('\\') || path.Contains('%') && !Uri.IsWellFormedUriString("http://localhost" + path, UriKind.Absolute)) return null;
        try { path = Uri.UnescapeDataString(path); } catch (UriFormatException) { return null; }
        if (path == "/") return "";
        string[] parts = path[1..].Split('/');
        if (parts.Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(' ')
            || part.Any(c => c < ' ' || c is '\\' or ':' or '%' or '?' or '#' or '<' or '>' or '|' or '"' or '*'))) return null;
        return string.Join('/', parts);
    }

    public byte[] Read(FrontendAsset asset)
    {
        if (!_files.TryGetValue(asset.Path, out FrontendAsset? indexed) || indexed != asset) throw new InvalidDataException("Unknown frontend asset");
        using Stream stream = _open(asset.ResourceName) ?? throw new InvalidDataException("Missing frontend resource");
        byte[] bytes = new byte[checked((int)asset.SizeBytes)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != asset.Sha256)
            throw new InvalidDataException("Frontend resource hash mismatch");
        return bytes;
    }
}
