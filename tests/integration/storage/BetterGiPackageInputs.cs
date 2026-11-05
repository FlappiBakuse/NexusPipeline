using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class BetterGiPackageInputs : IDisposable
{
    private readonly List<FileStream> _held = [];
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly byte[] _script;
    private readonly Dictionary<string, byte[]> _localization = new(StringComparer.Ordinal);
    internal byte[] ManifestBytes { get; }
    internal JsonObject Manifest { get; }

    internal BetterGiPackageInputs()
    {
        _root = Path.GetFullPath(Required("NEXUS_FORMAL_BETTERGI_INPUT_ROOT"));
        if (!Directory.Exists(_root) || string.Equals(_root.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(_root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("formal_bettergi_input_root_invalid");
        try
        {
            using var build = JsonDocument.Parse(Pin(Required("NEXUS_FORMAL_BETTERGI_BUILD_RECEIPT")));
            var receipt = build.RootElement; Assert.Equal("PASS", receipt.GetProperty("status").GetString());
            var source = receipt.GetProperty("source");
            _ = Pin(source.GetProperty("sourceManifestPath").GetString()!, source.GetProperty("sourceSnapshotHash").GetString());
            ManifestBytes = Identity(receipt.GetProperty("manifest")); _script = Identity(receipt.GetProperty("script"));
            var package = receipt.GetProperty("sdkPackage"); var packagePath = Owned(package.GetProperty("path").GetString()!);
            _ = Pin(packagePath, package.GetProperty("sha256").GetString());
            using (var archive = ZipFile.OpenRead(packagePath))
            using (var input = archive.GetEntry("lib/net10.0/NexusPipeline.Extensibility.dll")!.Open())
            using (var actual = File.OpenRead(typeof(IExtension).Assembly.Location))
                Assert.Equal(SHA256.HashData(actual), SHA256.HashData(input));
            Manifest = JsonNode.Parse(ManifestBytes)!.AsObject();
            foreach (var locale in Manifest["localization"]!["locales"]!.AsObject())
            {
                var relative = locale.Value!.GetValue<string>();
                var expected = receipt.GetProperty("generatorInputs").EnumerateArray()
                    .Single(item => item.GetProperty("path").GetString() == relative).GetProperty("sha256").GetString();
                _localization.Add(relative, Pin(Path.Combine(source.GetProperty("exportRoot").GetString()!,
                    "plugins", "specialized", "bettergi", relative), expected));
            }
            Assert.Equal("bettergi", Manifest["extensionId"]!.GetValue<string>());
            Assert.Equal("data-specialized", Manifest["kind"]!.GetValue<string>());
            Assert.Equal(SdkIdentity.ContractFingerprint, Manifest["contractFingerprint"]!.GetValue<string>());
            Assert.Equal(SdkIdentity.Version, Manifest["sdkVersion"]!.GetValue<string>());
            Assert.Null(Manifest["execution"]);
        }
        catch { Dispose(); throw; }
    }

    internal void Populate(string directory)
    {
        var path = Path.Combine(directory, Manifest["specialized"]!["adapterEntry"]!.GetValue<string>());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, _script);
        foreach (var (relative, bytes) in _localization)
        {
            var file = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes);
        }
    }

    internal void VerifyUnchanged()
    {
        foreach (var input in _held)
        { input.Position = 0; Assert.Equal(_hashes[input.Name], Convert.ToHexStringLower(SHA256.HashData(input))); }
    }

    private byte[] Identity(JsonElement value) => Pin(value.GetProperty("path").GetString()!, value.GetProperty("sha256").GetString());
    private byte[] Pin(string path, string? expected = null)
    {
        path = Owned(path); var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); _held.Add(file);
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (expected is not null) Assert.Equal(expected, digest);
        _hashes[path] = digest; return bytes;
    }
    private string Owned(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("formal_bettergi_input_outside_root");
        for (var parent = _root; parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("formal_bettergi_input_link");
        var walk = _root;
        if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) throw new IOException("formal_bettergi_input_link");
        foreach (var component in Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar))
        {
            walk = Path.Combine(walk, component);
            if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) throw new IOException("formal_bettergi_input_link");
        }
        return path;
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new IOException(name + "_required");
    public void Dispose() { foreach (var file in _held.AsEnumerable().Reverse()) file.Dispose(); }
}
