using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PackageArchiveTests
{
    [Theory(DisplayName = "Zip Checksum Matches The Standard Independent Known Vectors")]
    [InlineData("", 0U)]
    [InlineData("123456789", 0xcbf43926U)]
    public void MatchesIndependentChecksums(string value, uint expected)
    {
        var crc = new ExtensionZipChecksum(); crc.Append(Encoding.ASCII.GetBytes(value));
        Assert.Equal(expected, crc.Value);
    }

    [Theory(DisplayName = "Deflate Entries Keep Their Actual Content Checks With Or Without Data Descriptors")]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiesDeflateContent(bool descriptor)
    {
        var content = Enumerable.Range(0, 32768).Select(index => (byte)index).ToArray();
        using var input = new MemoryStream(SafeZipFixture.BuildCompressed(descriptor, content), writable: false);
        using var archive = ExtensionPackageArchive.Open(input);
        var file = Assert.Single(archive.Files, item => item.RelativePath == "data.bin");
        Assert.Equal(content.LongLength, file.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), file.Sha256);
        Assert.Equal("{}"u8.ToArray(), archive.ReadManifest());
    }

    [Fact(DisplayName = "A Verified Archive Candidate Uses The Existing Cas And File Tree Materializer With Implicit Parents")]
    public void MaterializesVerifiedArchives()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader(); var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        _ = NexusPipeline.Platform.Storage.Sqlite.StateDatabase.CreateFresh(instance);
        var data = Enumerable.Range(0, 65536).Select(index => (byte)index).ToArray();
        var bytes = SafeZipFixture.Build(new ExtensionZipSpec("nested/deeper/value.bin", data));
        using var input = new MemoryStream(bytes, writable: false);
        using var archive = ExtensionPackageArchive.Open(input);
        Assert.Equal(new[] { "", "nested", "nested/deeper" }, archive.Directories);
        var captured = archive.CaptureCandidate(new(instance, contracts), CancellationToken.None);
        var path = Path.Combine(root, "candidate");
        _ = new FileTreeArchive(new(SnapshotBudget.Default), new(instance, contracts))
            .MaterializeNew(captured, path, CancellationToken.None);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(path, "nested", "deeper", "value.bin")));
        Assert.Equal("{}"u8.ToArray(), File.ReadAllBytes(Path.Combine(path, "manifest.json")));
        var expected = Assert.Single(archive.Files, item => item.RelativePath == "nested/deeper/value.bin");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), expected.Sha256);
        Assert.Throws<IOException>(() => new FileTreeArchive(new(SnapshotBudget.Default), new(instance, contracts))
            .MaterializeNew(captured, path, CancellationToken.None));
    }

    [Theory(DisplayName = "No Archive Path Can Escape Or Alias An Existing File Or Directory Node")]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("backslash")]
    [InlineData("case-file")]
    [InlineData("duplicate-directory")]
    [InlineData("file-directory")]
    [InlineData("ancestor-file")]
    [InlineData("case-directory")]
    public void RejectsPathAliases(string fault)
    {
        ExtensionZipSpec[] entries = fault switch
        {
            "traversal" => [new("../outside", [1])],
            "absolute" => [new("/outside", [1])],
            "backslash" => [new("folder\\child", [1])],
            "case-file" => [new("a", [1]), new("A", [1])],
            "duplicate-directory" => [new("a/", []), new("a/", [])],
            "file-directory" => [new("a/", []), new("a", [1])],
            "ancestor-file" => [new("a", [1]), new("a/b", [1])],
            _ => [new("A/b", [1]), new("a/", [])]
        };
        var bytes = SafeZipFixture.Build(entries);
        using var input = new MemoryStream(bytes, writable: false);
        Assert.ThrowsAny<IOException>(() => ExtensionPackageArchive.Open(input));
        Assert.Equal(bytes, input.ToArray());
    }

    [Theory(DisplayName = "Directory Entries Have The Same Link And Payload Boundary As Files")]
    [InlineData("directory-data")]
    [InlineData("file-link")]
    [InlineData("directory-link")]
    [InlineData("dos-reparse")]
    [InlineData("directory-crc")]
    public void ValidatesDirectoryPayloads(string fault)
    {
        var spec = fault switch
        {
            "directory-data" => new ExtensionZipSpec("a/", [1]),
            "file-link" => new("a", [], unchecked((int)0xa0000000)),
            "directory-link" => new("a/", [], unchecked((int)0xa0000000)),
            "directory-crc" => new("a/", []),
            _ => new("a/", [], 0x400)
        };
        var bytes = SafeZipFixture.Build(spec);
        if (fault == "directory-crc")
        {
            var central = SafeZipFixture.Central(bytes);
            var next = central + 46 + SafeZipFixture.U16(bytes, central + 28) +
                SafeZipFixture.U16(bytes, central + 30) + SafeZipFixture.U16(bytes, central + 32);
            var local = checked((int)SafeZipFixture.U32(bytes, next + 42));
            SafeZipFixture.W32(bytes, next + 16, 1); SafeZipFixture.W32(bytes, local + 14, 1);
        }
        using var input = new MemoryStream(bytes, writable: false);
        Assert.ThrowsAny<IOException>(() => ExtensionPackageArchive.Open(input));
    }

    [Theory(DisplayName = "Unsupported Or Inconsistent Metadata Is Rejected Before Bcl Entry Materialization")]
    [InlineData("zip64")]
    [InlineData("multi-disk")]
    [InlineData("offset")]
    [InlineData("truncated")]
    [InlineData("entries")]
    [InlineData("ratio")]
    [InlineData("unknown-flags")]
    [InlineData("unreferenced-gap")]
    public void RejectsInconsistentMetadata(string fault)
    {
        var bytes = SafeZipFixture.Build(); var central = SafeZipFixture.Central(bytes);
        var end = bytes.Length - 22;
        switch (fault)
        {
            case "zip64": SafeZipFixture.W16(bytes, end + 8, ushort.MaxValue); SafeZipFixture.W16(bytes, end + 10, ushort.MaxValue); break;
            case "multi-disk": SafeZipFixture.W16(bytes, end + 4, 1); break;
            case "offset": SafeZipFixture.W32(bytes, end + 16, uint.MaxValue); break;
            case "truncated": bytes = bytes[..^1]; break;
            case "entries": SafeZipFixture.W16(bytes, end + 8, 4097); SafeZipFixture.W16(bytes, end + 10, 4097); break;
            case "unknown-flags": SafeZipFixture.W16(bytes, central + 8, 16); break;
            case "unreferenced-gap":
                bytes = bytes[..central].Concat("junk"u8.ToArray()).Concat(bytes[central..]).ToArray();
                SafeZipFixture.W32(bytes, bytes.Length - 6, checked((uint)central + 4)); break;
            default: SafeZipFixture.W32(bytes, central + 24, 250000000); break;
        }
        using var input = new MemoryStream(bytes, writable: false);
        Assert.ThrowsAny<IOException>(() => ExtensionPackageArchive.Open(input));
    }

    [Theory(DisplayName = "File Entry And Implicit Tree Budgets Cannot Be Split Into Independent Allowances")]
    [InlineData("files")]
    [InlineData("entries")]
    [InlineData("tree")]
    public void SharesTreeBudget(string fault)
    {
        var specs = fault switch
        {
            "files" => Enumerable.Range(0, ExtensionPackageArchive.MaximumFiles)
                .Select(index => new ExtensionZipSpec("f" + index, [1])),
            "entries" => Enumerable.Range(0, ExtensionZipMetadata.MaximumEntries)
                .Select(index => new ExtensionZipSpec("d" + index + "/", [])),
            _ => Enumerable.Range(0, 32).Select(index => new ExtensionZipSpec(
                "r" + index + "/" + string.Join("/", Enumerable.Repeat("d", 128)) + "/f", [1]))
        };
        using var input = new MemoryStream(SafeZipFixture.Build(specs.ToArray()), writable: false);
        Assert.ThrowsAny<IOException>(() => ExtensionPackageArchive.Open(input));
    }

    [Fact(DisplayName = "Matching Local And Central Crc Metadata Cannot Hide Actual Bad Content On The Target Runtime")]
    public void RejectsForgedCrc()
    {
        var bytes = SafeZipFixture.Build(); var central = SafeZipFixture.Central(bytes);
        var incorrect = SafeZipFixture.U32(bytes, central + 16) ^ 1;
        SafeZipFixture.W32(bytes, central + 16, incorrect); SafeZipFixture.W32(bytes, 14, incorrect);
        using var input = new MemoryStream(bytes, writable: false);
        Assert.Throws<IOException>(() => ExtensionPackageArchive.Open(input));
    }

    [Fact(DisplayName = "The Existing Provider Still Holds Its Actual Archive And Every Deployed File After Shared Zip Verification")]
    public void HoldsDeployedFiles()
    {
        using var fixture = new ProviderPackageFixture();
        var package = fixture.Verify(); var before = fixture.Fingerprint();
        using (package.Hold())
        {
            Assert.Throws<IOException>(() => File.Open(fixture.Archive, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            Assert.Throws<IOException>(() => File.Open(fixture.Worker, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        }
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Theory(DisplayName = "Cancellation Before Or During Content Cannot Return A Verified Archive")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsCancelledVerification(bool during)
    {
        var bytes = SafeZipFixture.Build(); using var stop = new CancellationTokenSource();
        using MemoryStream input = during ? new SafeZipFixture.CancelOnContent(bytes, stop) : new MemoryStream(bytes, writable: false);
        if (!during) stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ExtensionPackageArchive.Open(input, stop.Token));
        if (during) Assert.True(((SafeZipFixture.CancelOnContent)input).HasReadEntryContent);
        Assert.Equal(bytes, input.ToArray());
    }
}
