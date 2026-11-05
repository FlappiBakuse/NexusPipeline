using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class SpecializedPackageInputs : IDisposable
{
    private readonly string _root;
    private readonly List<FileStream> _held = [];
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    internal string Archive { get; }
    internal string? NativeArchive { get; }
    internal byte[] ManifestBytes { get; }
    internal JsonObject Manifest { get; }

    internal SpecializedPackageInputs(string extensionId, string receiptVariable, string kind = "data-specialized", bool nativeArchive = false)
    {
        _root = Path.GetFullPath(Required("NEXUS_FORMAL_SPECIALIZED_ROOT"));
        if (!Directory.Exists(_root) || Path.GetPathRoot(_root) == _root) throw new IOException("specialized_input_root_invalid");
        try
        {
            using var document = JsonDocument.Parse(Pin(Required(receiptVariable)));
            var receipt = document.RootElement;
            if (nativeArchive)
            {
                Assert.Equal("maa-framework", extensionId);
                var runtime = receipt.GetProperty("runtimeInput");
                using var native = JsonDocument.Parse(Pin(runtime.GetProperty("inputReceipt").GetString()!,
                    runtime.GetProperty("inputReceiptSha256").GetString(), native: true));
                NativeArchive = native.RootElement.GetProperty("archive").GetString()!;
                _ = Pin(NativeArchive, runtime.GetProperty("releasePin").GetProperty("assetSha256").GetString(), native: true);
            }
            Assert.Equal("PASS", receipt.GetProperty("status").GetString());
            Assert.Equal(extensionId, receipt.GetProperty("extensionId").GetString());
            Assert.Equal(SdkIdentity.ContractFingerprint, receipt.GetProperty("contractFingerprint").GetString());
            var source = receipt.GetProperty("source");
            _ = Pin(source.GetProperty("sourceManifestPath").GetString()!, source.GetProperty("sourceSnapshotHash").GetString());
            using var build = JsonDocument.Parse(Pin(receipt.GetProperty("buildReceipt").GetString()!, receipt.GetProperty("buildReceiptSha256").GetString()));
            Assert.Equal("PASS", build.RootElement.GetProperty("status").GetString());
            var sdk = build.RootElement.GetProperty("sdkPackage");
            var sdkPath = sdk.GetProperty("path").GetString()!;
            _ = Pin(sdkPath, sdk.GetProperty("sha256").GetString());
            using (var package = ZipFile.OpenRead(sdkPath))
            using (var input = package.GetEntry("lib/net10.0/NexusPipeline.Extensibility.dll")!.Open())
            using (var actual = File.OpenRead(typeof(IExtension).Assembly.Location))
                Assert.Equal(SHA256.HashData(actual), SHA256.HashData(input));
            var artifact = receipt.GetProperty("artifact");
            Archive = artifact.GetProperty("path").GetString()!;
            var archiveBytes = Pin(Archive, artifact.GetProperty("sha256").GetString());
            Assert.Equal(artifact.GetProperty("sizeBytes").GetInt64(), archiveBytes.LongLength);
            using var bytes = new MemoryStream(archiveBytes);
            using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
            var members = receipt.GetProperty("members").EnumerateArray().ToArray();
            Assert.Equal(members.Length, zip.Entries.Count);
            foreach (var member in members)
            {
                var entry = Assert.Single(zip.Entries, item => item.FullName == member.GetProperty("path").GetString());
                Assert.Equal(member.GetProperty("sizeBytes").GetInt64(), entry.Length);
                using var input = entry.Open();
                Assert.Equal(member.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(input)));
            }
            using var manifestInput = zip.GetEntry("manifest.json")!.Open();
            using var manifestBytes = new MemoryStream(); manifestInput.CopyTo(manifestBytes);
            ManifestBytes = manifestBytes.ToArray(); Manifest = JsonNode.Parse(ManifestBytes)!.AsObject();
            Assert.Equal(extensionId, Manifest["extensionId"]!.GetValue<string>());
            Assert.Equal(kind, Manifest["kind"]!.GetValue<string>());
            Assert.Equal(SdkIdentity.ContractFingerprint, Manifest["contractFingerprint"]!.GetValue<string>());
        }
        catch { Dispose(); throw; }
    }

    internal void VerifyUnchanged()
    {
        foreach (var stream in _held)
        { stream.Position = 0; Assert.Equal(_hashes[stream.Name], Convert.ToHexStringLower(SHA256.HashData(stream))); }
    }

    private byte[] Pin(string value, string? expected = null, bool native = false)
    {
        var path = Path.GetFullPath(value);
        var root = native ? Path.GetFullPath(Path.Combine(_root, "../cache/maa-framework")) : _root;
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("specialized_input_outside_root");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("specialized_input_link");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); _held.Add(file);
        using var output = new MemoryStream(); file.CopyTo(output); var bytes = output.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (expected is not null) Assert.Equal(expected, hash);
        _hashes[path] = hash; return bytes;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new IOException(name + "_required");
    public void Dispose() { foreach (var file in _held.AsEnumerable().Reverse()) file.Dispose(); }
}
