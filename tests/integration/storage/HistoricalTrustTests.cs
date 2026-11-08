using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class HistoricalTrustTests
{
    [Theory(DisplayName = "A Newer Accepted Head Or Missing Latest Entry Does Not Revoke The Recorded Catalog")]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsRecordedCatalogs(bool removed)
    {
        using var fixture = new BoundPackageFixture();
        fixture.Publish();
        var original = Saved(fixture); var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        var next = fixture.Signed(2, removed ? value =>
        {
            value["extensionId"] = "other-fixture"; value["artifactName"] = "OtherFixture";
        } : null);
        _ = fixture.Catalog.Store.Accept(Guid.NewGuid(), "local", fixture.Catalog.Verify(next));
        using var root = fixture.Acquire();
        using var history = fixture.Catalog.Store.ProtectInstalledHistory(root, original, entry);
        Assert.Equal(2, Saved(fixture).Identity.Sequence);
        Assert.Equal(1, original.Identity.Sequence);
    }

    [Fact(DisplayName = "A New Admission Floor Does Not Create New Authority Or Revoke An Already Recorded History")]
    public void SeparatesAdmissionFloors()
    {
        using var fixture = new BoundPackageFixture();
        var signed = fixture.Signed(); _ = fixture.Catalog.Store.Accept(Guid.NewGuid(), "local", fixture.Catalog.Verify(signed));
        var original = Saved(fixture); var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        var verifier = fixture.Catalog.NewVerifier(fixture.Catalog.Signer.ExportSubjectPublicKeyInfo(), stableFloor: 99);
        Assert.Equal("extension_catalog_rollback_rejected", Assert.Throws<InvalidOperationException>(() =>
            verifier.Verify(signed.Pointer, signed.Catalog, signed.Signature, ExtensionStoreChannel.Stable)).Message);
        var store = fixture.Catalog.NewStore(verifier);
        using var root = fixture.Acquire();
        using var history = store.ProtectInstalledHistory(root, original, entry);
        Assert.Equal(original, Saved(fixture));
        Assert.Equal(1, fixture.Catalog.Count("extension_catalog_acceptances"));
    }

    [Fact(DisplayName = "The Current Key Set Still Rejects A Recorded Signature Whose Key Is No Longer Trusted")]
    public void RechecksRevokedKeys()
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var original = Saved(fixture); var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = fixture.Catalog.NewStore(fixture.Catalog.NewVerifier(other.ExportSubjectPublicKeyInfo()));
        using var root = fixture.Acquire();
        Assert.Throws<IOException>(() => { using var history = store.ProtectInstalledHistory(root, original, entry); });
        Assert.Equal(original, Saved(fixture));
    }

    [Theory(DisplayName = "Broken Current Head Or Current Signed Bytes Cannot Be Hidden Behind Valid Old History")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsBrokenHeads(bool bytes)
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var original = Saved(fixture); var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        fixture.Publish(2); var current = Saved(fixture);
        var path = Path.Combine(fixture.Catalog.Instance.RuntimeRoot, current.Catalog.RelativePath);
        var originalBytes = File.ReadAllBytes(path);
        using var root = fixture.Acquire();
        if (bytes)
        {
            var changed = originalBytes.ToArray(); changed[^2] ^= 1; File.WriteAllBytes(path, changed);
        }
        else fixture.Catalog.Sql("UPDATE extension_catalog_heads SET sequence=1,catalog_sha='" + original.Identity.Sha256 + "' WHERE channel='stable';");
        try { Assert.Throws<IOException>(() => { using var history = fixture.Catalog.Store.ProtectInstalledHistory(root, original, entry); }); }
        finally
        {
            if (bytes) File.WriteAllBytes(path, originalBytes);
            else fixture.Catalog.Sql("UPDATE extension_catalog_heads SET sequence=2,catalog_sha='" + current.Identity.Sha256 + "' WHERE channel='stable';");
        }
        using var history = fixture.Catalog.Store.ProtectInstalledHistory(root, original, entry);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "Another Instances Real Resource Lease Does Not Authorize Recorded History Use")]
    public void RequiresInstanceLease()
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        using var other = new BoundPackageFixture(); other.Publish();
        var original = Saved(fixture); var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        using var root = other.Acquire();
        Assert.Throws<InvalidOperationException>(() => { using var history = fixture.Catalog.Store.ProtectInstalledHistory(root, original, entry); });
        Assert.Equal(original, Saved(fixture));
    }

    private static ExtensionCatalogSaved Saved(BoundPackageFixture fixture)
    {
        using var connection = DatabaseConnections.Open(fixture.Catalog.Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return ExtensionCatalogRows.ReadHead(connection, transaction, ExtensionStoreChannel.Stable)!;
    }
}
