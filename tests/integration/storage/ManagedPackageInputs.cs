using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class ManagedPackageInputs : IDisposable
{
    private readonly string _root;
    private readonly List<FileStream> _held = [];
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _assembly;
    internal JsonObject Manifest { get; }
    internal byte[] ManifestBytes { get; }

    internal ManagedPackageInputs(string extensionId, string receiptVariable)
    {
        _root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_FORMAL_MANAGED_INPUT_ROOT") ??
            throw new IOException("NEXUS_FORMAL_MANAGED_INPUT_ROOT_required"));
        if (!Directory.Exists(_root) || string.Equals(_root.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetPathRoot(_root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw Invalid();
        try
        {
            using var build = JsonDocument.Parse(Pin(Required(receiptVariable)));
            var receipt = build.RootElement; Assert.Equal("PASS", receipt.GetProperty("status").GetString());
            var source = receipt.GetProperty("source");
            _ = Pin(source.GetProperty("sourceManifestPath").GetString()!, source.GetProperty("sourceSnapshotHash").GetString());
            var identity = receipt.GetProperty("assembly"); _assembly = Owned(identity.GetProperty("path").GetString()!);
            _ = Pin(_assembly, identity.GetProperty("sha256").GetString());
            var manifest = receipt.GetProperty("manifest");
            ManifestBytes = Pin(manifest.GetProperty("path").GetString()!, manifest.GetProperty("sha256").GetString());
            var package = receipt.GetProperty("sdkPackage"); var packagePath = Owned(package.GetProperty("path").GetString()!);
            _ = Pin(packagePath, package.GetProperty("sha256").GetString());
            using (var archive = ZipFile.OpenRead(packagePath))
            using (var sdk = archive.GetEntry("lib/net10.0/NexusPipeline.Extensibility.dll")!.Open())
            using (var current = File.OpenRead(typeof(IExtension).Assembly.Location))
                Assert.Equal(SHA256.HashData(current), SHA256.HashData(sdk));
            Manifest = JsonNode.Parse(ManifestBytes)!.AsObject();
            Assert.Equal(extensionId, Manifest["extensionId"]!.GetValue<string>());
            Assert.Equal("managed-code", Manifest["kind"]!.GetValue<string>());
            Assert.Equal(SdkIdentity.Version, Manifest["sdkVersion"]!.GetValue<string>());
            Assert.Equal(SdkIdentity.ContractFingerprint, Manifest["contractFingerprint"]!.GetValue<string>());
            Assert.Equal(SdkIdentity.ContractFingerprint, receipt.GetProperty("contractFingerprint").GetString());
            Assert.Equal(Path.GetFileName(_assembly), Manifest["managed"]!["entryAssembly"]!.GetValue<string>());
            Assert.True(Manifest["managed"]!["nativeLibraries"] is null or JsonArray { Count: 0 });
        }
        catch { Dispose(); throw; }
    }

    internal void Populate(string directory) =>
        File.Copy(_assembly, Path.Combine(directory, Manifest["managed"]!["entryAssembly"]!.GetValue<string>()));

    internal void VerifyUnchanged()
    {
        foreach (var file in _held)
        { file.Position = 0; Assert.Equal(_hashes[file.Name], Convert.ToHexStringLower(SHA256.HashData(file))); }
    }

    private byte[] Pin(string value, string? expected = null)
    {
        var path = Owned(value); var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); _held.Add(file);
        var hash = Convert.ToHexStringLower(SHA256.HashData(file));
        if (expected is not null) Assert.Equal(expected, hash); _hashes.Add(path, hash);
        if (file.Length > 512 * 1024 && expected is not null) return [];
        file.Position = 0; using var bytes = new MemoryStream(); file.CopyTo(bytes); return bytes.ToArray();
    }
    private string Owned(string value)
    {
        var path = Path.GetFullPath(value);
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        return path;
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new IOException(name + "_required");
    private static IOException Invalid() => new("formal_managed_input_not_owned");
    public void Dispose() { foreach (var file in _held.AsEnumerable().Reverse()) file.Dispose(); }
}
