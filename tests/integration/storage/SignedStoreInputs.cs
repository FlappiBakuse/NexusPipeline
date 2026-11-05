using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class SignedStoreInputs : IDisposable
{
    private readonly string _receipt;
    private readonly byte[] _original;
    private readonly JsonDocument _document;
    internal string Bundle { get; }
    internal byte[] Pointer { get; }
    internal byte[] Catalog { get; }
    internal byte[] Signature { get; }
    internal IReadOnlyDictionary<string, byte[]> Files { get; }

    internal SignedStoreInputs()
    {
        _receipt = PhysicalPathPolicy.NormalizeWithoutLinks(Environment.GetEnvironmentVariable("NEXUS_PRODUCT_STORE")
            ?? throw new IOException("signed_store_receipt_required"));
        _original = File.ReadAllBytes(_receipt); _document = JsonDocument.Parse(_original);
        var record = _document.RootElement;
        Assert.Equal("PASS_LOCAL_SIGNED_STORE", record.GetProperty("status").GetString());
        Assert.Equal("test", record.GetProperty("profile").GetString());
        Assert.Equal("fixture-only", record.GetProperty("keyId").GetString());
        var parent = Path.GetDirectoryName(_receipt)!;
        Bundle = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(parent,
            record.TryGetProperty("bundleRelativePath", out var path) ? path.GetString()! : "bundle"));
        Assert.StartsWith(parent + Path.DirectorySeparatorChar, Bundle, StringComparison.OrdinalIgnoreCase);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in record.GetProperty("files").EnumerateArray())
        {
            var name = item.GetProperty("path").GetString()!;
            Assert.Equal(name, Path.GetFileName(name));
            var content = File.ReadAllBytes(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(Bundle, name)));
            Assert.Equal(item.GetProperty("sizeBytes").GetInt64(), content.LongLength);
            Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(content)));
            files.Add(name, content);
        }
        Assert.Equal(17, files.Count);
        Files = files; Pointer = files["index.json"]; Catalog = files["catalog.json"]; Signature = files["catalog.sig.json"];
        VerifyUnchanged();
    }

    internal void Populate(StoreMirror mirror)
    {
        using var pointer = JsonDocument.Parse(Pointer);
        mirror.Files[new Uri(ExtensionCatalogFixture.MetadataRoot, "stable/index.json").AbsoluteUri] = Pointer;
        mirror.Files[pointer.RootElement.GetProperty("catalogUrl").GetString()!] = Catalog;
        mirror.Files[pointer.RootElement.GetProperty("signatureUrl").GetString()!] = Signature;
        using var catalog = JsonDocument.Parse(Catalog);
        foreach (var entry in catalog.RootElement.GetProperty("entries").EnumerateArray())
            mirror.Files[entry.GetProperty("artifact").GetProperty("url").GetString()!] = Files[
                entry.GetProperty("artifactName").GetString()! + "-" + entry.GetProperty("packageVersion").GetString()! + ".zip"];
    }

    internal void VerifyUnchanged()
    {
        Assert.Equal(_original, File.ReadAllBytes(_receipt));
        Assert.Equal(Files.Keys.Order(), Directory.GetFiles(Bundle).Select(Path.GetFileName).Order());
        foreach (var pair in Files)
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Bundle, pair.Key)))));
    }

    public void Dispose() => _document.Dispose();
}
