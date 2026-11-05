using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CatalogStoreTests
{
    [Fact(DisplayName = "Accepted Maximum And Generation Remain Independent For Each Channel Across An Actual Reopen")]
    public void SeparatesChannelFloors()
    {
        using var fixture = new ExtensionCatalogFixture(stableFloor: 5, developFloor: 2);
        var stable = fixture.Verify(fixture.Signed(5));
        var develop = fixture.Verify(fixture.Signed(2, "develop"), ExtensionStoreChannel.Develop);
        fixture.Store.Accept(Guid.NewGuid(), "fixture", stable); fixture.Store.Accept(Guid.NewGuid(), "fixture", develop);
        Assert.Throws<InvalidOperationException>(() => fixture.Verify(fixture.Signed(4)));
        var newer = fixture.Verify(fixture.Signed(6)); fixture.Store.Accept(Guid.NewGuid(), "fixture", newer);
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Accept(Guid.NewGuid(), "fixture", stable));
        Assert.Throws<IOException>(() => fixture.Verifier.Verify(develop.PointerBytes(), develop.CatalogBytes(),
            develop.SignatureBytes(), ExtensionStoreChannel.Stable));
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Instance.RuntimeRoot, "diagnostic"));
        Assert.Equal(fixture.Database.InstanceId, reopened.InstanceId);
        var reopenedStore = new ExtensionCatalogStore(fixture.Instance, reopened, fixture.Lease, fixture.Resources, fixture.Contracts, fixture.Verifier);
        Assert.Equal(newer.Identity, reopenedStore.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        Assert.Equal(develop.Identity, fixture.Store.ReadCached(ExtensionStoreChannel.Develop)!.Identity);
        Assert.Equal(3, fixture.Count("extension_catalog_acceptances")); Assert.Equal(2, fixture.Count("extension_catalog_heads"));
    }

    [Fact(DisplayName = "Same Sequence Cannot Replace The Original Signed Generation And Operation Replay Cannot Change Its Subject")]
    public void BindsSignedGenerations()
    {
        using var fixture = new ExtensionCatalogFixture();
        var original = fixture.Verify(fixture.Signed()); var operation = Guid.NewGuid();
        fixture.Store.Accept(operation, "fixture", original);
        fixture.Store.Accept(operation, "fixture", original);
        Assert.Throws<OperationConflictException>(() => fixture.Store.Accept(operation, "other-subject", original));
        var replacement = fixture.Verify(fixture.Signed());
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Accept(Guid.NewGuid(), "fixture", replacement));
        Assert.Equal(original.Identity, fixture.Store.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        Assert.Equal(1, fixture.Count("extension_catalog_acceptances")); Assert.Equal(1, fixture.Count("operations"));
    }

    [Fact(DisplayName = "An Actual Final Sql Failure Rolls Back The Watermark And Operation And Allows Only An Exact Retry")]
    public void RevertsSqlFailure()
    {
        using var fixture = new ExtensionCatalogFixture(); var operation = Guid.NewGuid();
        var original = fixture.Verify(fixture.Signed());
        fixture.Sql("CREATE TRIGGER fixture_catalog_fail BEFORE INSERT ON extension_catalog_heads " +
            "BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try { Assert.Throws<SqliteException>(() => fixture.Store.Accept(operation, "fixture", original)); }
        finally { fixture.Sql("DROP TRIGGER fixture_catalog_fail;"); }
        Assert.Equal(0, fixture.Count("extension_catalog_acceptances")); Assert.Equal(0, fixture.Count("extension_catalog_heads"));
        Assert.Equal(0, fixture.Count("operations")); Assert.Null(fixture.Store.ReadCached(ExtensionStoreChannel.Stable));
        fixture.Store.Accept(operation, "fixture", original);
        Assert.Equal(original.Identity, fixture.Store.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
    }

    [Theory(DisplayName = "Offline Cache Cannot Bypass The Current Key Policy Or Original Saved Bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksOfflineTrust(bool tamper)
    {
        using var fixture = new ExtensionCatalogFixture(); var original = fixture.Verify(fixture.Signed());
        fixture.Store.Accept(Guid.NewGuid(), "fixture", original);
        if (tamper)
        {
            var path = Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "plans", original.Identity.Sha256 + ".json");
            var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
            Assert.Throws<IOException>(() => fixture.Store.ReadCached(ExtensionStoreChannel.Stable));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        else
        {
            using var replacement = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var revoked = fixture.NewStore(fixture.NewVerifier(replacement.ExportSubjectPublicKeyInfo()));
            Assert.Throws<IOException>(() => revoked.ReadCached(ExtensionStoreChannel.Stable));
        }
        Assert.Equal(1, fixture.Count("extension_catalog_heads")); Assert.Equal(1, fixture.Count("extension_catalog_acceptances"));
    }

    [Fact(DisplayName = "Actual Metadata Lease Conflict And Host Draining Cannot Advance The Catalog")]
    public void RequiresMetadataAdmission()
    {
        using var fixture = new ExtensionCatalogFixture(); var original = fixture.Verify(fixture.Signed());
        using (var held = fixture.Resources.TryAcquire(Guid.NewGuid(), [new("extensions:catalog", ResourceClaimMode.Mutation)]).Lease)
        {
            Assert.NotNull(held);
            Assert.Throws<IOException>(() => fixture.Store.Accept(Guid.NewGuid(), "fixture", original));
            Assert.Equal(0, fixture.Count("operations"));
        }
        _ = fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<IOException>(() => fixture.Store.Accept(Guid.NewGuid(), "fixture", original));
        Assert.Equal(0, fixture.Count("extension_catalog_heads"));
    }

    [Fact(DisplayName = "A Dangling Sql Head Cannot Become An Empty Or Trusted Cache")]
    public void RejectsDanglingHeads()
    {
        using var fixture = new ExtensionCatalogFixture(); var original = fixture.Verify(fixture.Signed());
        fixture.Store.Accept(Guid.NewGuid(), "fixture", original);
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=OFF; DELETE FROM extension_catalog_acceptances;";
        command.ExecuteNonQuery();
        Assert.Throws<IOException>(() => fixture.Store.ReadCached(ExtensionStoreChannel.Stable));
        Assert.Equal(1, fixture.Count("extension_catalog_heads"));
    }
}
