using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class BuiltExtensionInputs : IDisposable
{
    private readonly string _root;
    private readonly List<(FileStream File, string Sha256)> _held = [];
    internal JsonObject Manifest { get; }
    internal byte[] ManifestBytes { get; }
    internal string Archive { get; }

    internal BuiltExtensionInputs(string extensionId, string receiptVariable, bool explicitPath = false)
    {
        _root = Path.GetFullPath(Required("NEXUS_PACKAGED_EXTENSION_ROOT"));
        if (!Directory.Exists(_root) || _root.TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(_root)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new IOException("packaged_extension_root_invalid");
        try
        {
            using var receipt = JsonDocument.Parse(Read(explicitPath ? receiptVariable : Required(receiptVariable)));
            var root = receipt.RootElement; Assert.Equal("PASS", root.GetProperty("status").GetString());
            Assert.Equal(extensionId, root.GetProperty("extensionId").GetString());
            var source = root.GetProperty("source");
            _ = Read(source.GetProperty("sourceManifestPath").GetString()!, source.GetProperty("sourceSnapshotHash").GetString());
            var buildPath = root.GetProperty("buildReceipt").GetString()!;
            _ = Read(buildPath, root.GetProperty("buildReceiptSha256").GetString());
            using var sdk = JsonDocument.Parse(Read(Path.Combine(Path.GetDirectoryName(buildPath)!, "sdk-consumer-receipt.json"),
                root.GetProperty("sdkReceiptSha256").GetString()));
            var nupkg = sdk.RootElement.GetProperty("packages").EnumerateArray().Single(item => item.GetProperty("path").GetString()!.EndsWith(".nupkg", StringComparison.Ordinal));
            var sdkPath = nupkg.GetProperty("path").GetString()!; Pin(sdkPath, nupkg.GetProperty("sha256").GetString());
            using (var package = ZipFile.OpenRead(sdkPath))
            using (var publicApi = package.GetEntry("lib/net10.0/NexusPipeline.Extensibility.dll")!.Open())
            using (var actual = File.OpenRead(typeof(IExtension).Assembly.Location))
                Assert.Equal(SHA256.HashData(actual), SHA256.HashData(publicApi));
            var artifact = root.GetProperty("artifact"); Archive = Owned(artifact.GetProperty("path").GetString()!);
            var original = Pin(Archive, artifact.GetProperty("sha256").GetString());
            Assert.Equal(artifact.GetProperty("sizeBytes").GetInt64(), original.Length);
            using var archive = ZipFile.OpenRead(Archive);
            var members = root.GetProperty("members").EnumerateArray().ToDictionary(item => item.GetProperty("path").GetString()!, StringComparer.Ordinal);
            Assert.Equal(members.Count, archive.Entries.Count);
            Assert.Equal(members.Count, archive.Entries.Select(item => item.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var entry in archive.Entries)
            {
                var expected = members[entry.FullName]; Assert.Equal(expected.GetProperty("sizeBytes").GetInt64(), entry.Length);
                using var input = entry.Open(); Assert.Equal(expected.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(input)));
            }
            var manifestEntry = archive.GetEntry("manifest.json")!;
            if (manifestEntry.Length > 1048576) throw new IOException("packaged_manifest_size_invalid");
            using (var input = manifestEntry.Open())
            {
                using var bytes = new MemoryStream(); input.CopyTo(bytes); ManifestBytes = bytes.ToArray();
            }
            Assert.Equal(root.GetProperty("manifestSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(ManifestBytes)));
            Manifest = JsonNode.Parse(ManifestBytes)!.AsObject();
            Assert.Equal(extensionId, Manifest["extensionId"]!.GetValue<string>());
            Assert.Equal(SdkIdentity.Version, Manifest["sdkVersion"]!.GetValue<string>());
            Assert.Equal(SdkIdentity.ContractFingerprint, Manifest["contractFingerprint"]!.GetValue<string>());
        }
        catch { Dispose(); throw; }
    }

    internal void VerifyUnchanged()
    {
        foreach (var (file, digest) in _held)
        { file.Position = 0; Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(file))); }
    }

    private byte[] Read(string path, string? digest = null)
    {
        var file = Pin(path, digest);
        if (file.Length > 2097152) throw new IOException("packaged_evidence_size_invalid");
        file.Position = 0; var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }
    private FileStream Pin(string path, string? digest)
    {
        var file = new FileStream(Owned(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        _held.Add((file, digest ?? "")); var actual = Convert.ToHexStringLower(SHA256.HashData(file));
        if (digest is not null) Assert.Equal(digest, actual);
        _held[^1] = (file, actual); return file;
    }
    private string Owned(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("packaged_evidence_outside_root");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("packaged_evidence_has_link");
        return path;
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new IOException(name + "_required");
    public void Dispose() { foreach (var (file, _) in _held.AsEnumerable().Reverse()) file.Dispose(); }
}
