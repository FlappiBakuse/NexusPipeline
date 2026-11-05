using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PackageAuthorityTests
{
    [Theory(DisplayName = "Only The Accepted Signed Entry Can Prepare An Immutable Unactivated Candidate")]
    [InlineData("managed-code")]
    [InlineData("data-specialized")]
    public void RequiresAcceptedEntry(string kind)
    {
        using var fixture = new BoundPackageFixture(kind); fixture.Publish();
        using var root = fixture.Acquire(); var before = fixture.Fingerprint();
        using var package = fixture.Prepare(root);
        var candidate = package.CaptureCandidate(root, fixture.Blobs, CancellationToken.None);
        var bytes = package.ManifestBytes(); bytes[0] = 0;
        var returned = package.Manifest; ((IList<string>)returned.Capabilities).Add("media.capture");
        var count = candidate.Use(root, fixture.Blobs, CancellationToken.None, (binding, tree) =>
        {
            Assert.Equal(fixture.OperationId, binding.OperationId);
            Assert.Equal(fixture.Catalog.Instance.InstanceId, binding.InstanceId);
            Assert.Throws<NotSupportedException>(() => ((IList<NexusPipeline.Core.Configuration.FileTreeEntry>)tree.Snapshot.Entries).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<BlobReference>)tree.Contents).Clear());
            return tree.Snapshot.Entries.Count;
        });
        Assert.Equal(3, count);
        Assert.Empty(package.Manifest.Capabilities);
        Assert.Equal(fixture.ManifestBytes, package.ManifestBytes());
        Assert.Throws<IOException>(() => File.Open(fixture.Archive, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact(DisplayName = "A Valid Signature Without The Accepted Catalog Head Does Not Mint A Package Authority")]
    public void RequiresCatalogAuthority()
    {
        using var fixture = new BoundPackageFixture();
        var signedButUnaccepted = fixture.Catalog.Verify(fixture.Signed());
        Assert.Single(signedButUnaccepted.Entries);
        using var root = fixture.Acquire();
        Assert.Throws<IOException>(() => fixture.Prepare(root));
        Assert.Equal(0, fixture.Catalog.Count("extension_catalog_heads"));
    }

    [Theory(DisplayName = "A Trusted Catalog Still Rejects An Unbound Archive Or Original Manifest")]
    [InlineData("extensionId")]
    [InlineData("artifactName")]
    [InlineData("packageVersion")]
    [InlineData("kind")]
    [InlineData("minHostVersion")]
    [InlineData("manifestSha256")]
    [InlineData("artifactSha256")]
    public void RejectsUnboundArchives(string field)
    {
        using var fixture = new BoundPackageFixture();
        fixture.Publish(entryEdit: entry =>
        {
            switch (field)
            {
                case "extensionId": entry[field] = "another-plugin"; break;
                case "artifactName": entry[field] = "AnotherPlugin"; break;
                case "packageVersion": entry[field] = "0.5.1"; break;
                case "kind": entry[field] = "data-specialized"; break;
                case "minHostVersion": entry[field] = "0.17.0"; break;
                case "manifestSha256": entry[field] = new string('d', 64); break;
                default: entry["artifact"]!["sha256"] = new string('e', 64); break;
            }
        });
        using var root = fixture.Acquire(); var before = fixture.Fingerprint();
        Assert.Throws<IOException>(() => fixture.Prepare(root));
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Theory(DisplayName = "Full Manifest Schema And Declared Payload Boundaries Remain Required After Signature Verification")]
    [InlineData("unknown-capability")]
    [InlineData("wrong-sdk")]
    [InlineData("mixed-kind")]
    [InlineData("specialized-frontend")]
    [InlineData("missing-entry")]
    [InlineData("missing-frontend")]
    public void RequiresDeclaredBoundaries(string fault)
    {
        var kind = fault == "specialized-frontend" ? "data-specialized" : "managed-code";
        using var fixture = new BoundPackageFixture(kind, manifest =>
        {
            switch (fault)
            {
                case "unknown-capability": manifest["capabilities"] = new JsonArray("unrecognized.capability"); break;
                case "wrong-sdk": manifest["sdkVersion"] = "0.2.0"; break;
                case "mixed-kind": manifest["specialized"] = new JsonObject { ["adapterEntry"] = "adapter.js" }; break;
                case "missing-entry": manifest["managed"]!["entryAssembly"] = "missing.dll"; break;
                default:
                    manifest["capabilities"] = new JsonArray("ui.contribute");
                    manifest["frontend"] = new JsonObject { ["entry"] = "missing.js", ["styles"] = new JsonArray(), ["sdkVersion"] = "0.1.0" };
                    break;
            }
        });
        fixture.Publish(); using var root = fixture.Acquire(); var before = fixture.Fingerprint();
        if (fault is "missing-entry" or "missing-frontend") Assert.Throws<IOException>(() => fixture.Prepare(root));
        else Assert.Throws<JsonException>(() => fixture.Prepare(root));
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Theory(DisplayName = "Actual Resource Borrow And Same Instance Are Required Again For Candidate Consumption")]
    [InlineData("other-owner")]
    [InlineData("other-operation")]
    [InlineData("missing-claim")]
    [InlineData("disposed-root")]
    [InlineData("other-content-instance")]
    public void RechecksCandidateOwnership(string fault)
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        using var root = fixture.Acquire(); using var package = fixture.Prepare(root);
        var candidate = package.CaptureCandidate(root, fixture.Blobs, CancellationToken.None);
        if (fault == "other-content-instance")
        {
            using var other = new ExtensionCatalogFixture();
            Assert.Throws<IOException>(() => candidate.Use(root, new(other.Instance, other.Contracts), CancellationToken.None, (_, _) => true));
        }
        else if (fault == "other-owner")
        {
            using var other = new BoundPackageFixture(); using var wrong = other.Acquire(fixture.OperationId);
            Assert.Throws<InvalidOperationException>(() => candidate.Use(wrong, fixture.Blobs, CancellationToken.None, (_, _) => true));
        }
        else if (fault == "disposed-root")
        {
            root.Dispose();
            Assert.Throws<ObjectDisposedException>(() => candidate.Use(root, fixture.Blobs, CancellationToken.None, (_, _) => true));
        }
        else
        {
            root.Dispose(); using var wrong = fixture.Acquire(fault == "other-operation" ? Guid.NewGuid() : fixture.OperationId,
                catalogClaim: fault != "missing-claim");
            Assert.Throws<InvalidOperationException>(() => candidate.Use(wrong, fixture.Blobs, CancellationToken.None, (_, _) => true));
        }
    }

    [Fact(DisplayName = "A New Accepted Catalog Cannot Be Replaced By A Former Prepared Candidate")]
    public void RejectsStaleCandidates()
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var root = fixture.Acquire(); using var package = fixture.Prepare(root);
        var candidate = package.CaptureCandidate(root, fixture.Blobs, CancellationToken.None);
        root.Dispose(); fixture.Publish(sequence: 2); using var fresh = fixture.Acquire();
        Assert.Throws<IOException>(() => candidate.Use(fresh, fixture.Blobs, CancellationToken.None, (_, _) => true));
    }

    [Fact(DisplayName = "Disposed Package Cannot Authorize Its Former Verified Candidate")]
    public void RejectsDisposedPackages()
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        using var root = fixture.Acquire(); using var package = fixture.Prepare(root);
        var candidate = package.CaptureCandidate(root, fixture.Blobs, CancellationToken.None); package.Dispose();
        Assert.Throws<ObjectDisposedException>(() => candidate.Use(root, fixture.Blobs, CancellationToken.None, (_, _) => true));
    }

    [Fact(DisplayName = "The Manifest Reread Requires Original Sha Even For Equal Length Equal Crc Payloads")]
    public void VerifiesOriginalManifest()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var output = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
            output.Write("plumless"u8);
        }
        var original = bytes.ToArray();
        Assert.Equal((ushort)0, SafeZipFixture.U16(original, 8));
        Assert.Equal(0x4ddb0c25U, SafeZipFixture.U32(original, SafeZipFixture.Central(original) + 16));
        using var mutable = new MemoryStream(original, writable: true);
        using var archive = ExtensionPackageArchive.Open(mutable);
        mutable.Position = SafeZipFixture.Data(original); mutable.Write("buckeroo"u8);
        Assert.Equal("extension_package_content_invalid", Assert.Throws<IOException>(() => archive.ReadManifest()).Message);
        Assert.Equal("buckeroo", Encoding.ASCII.GetString(original, SafeZipFixture.Data(original), 8));
    }
}
