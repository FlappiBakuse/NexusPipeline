using System.Runtime.Versioning;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class IdentityProofTests
{
    [Fact(DisplayName = "Protected Identity Proofs Deduplicate The Same Authentication Across Task Subjects And Separate Fields Versions And Instances")]
    public void SharesCredentialIdentity()
    {
        using var fixture = new ExtensionCatalogFixture(); using var other = new ExtensionCatalogFixture();
        using var first = new SecretValue("synthetic-canonical-auth"); using var second = new SecretValue("synthetic-other-auth");
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        var key = new SecretFieldKey("fixture-plugin", Guid.NewGuid(), "account");
        var proof = SecretReplayProof.Identity(fixture.Instance, connection, key, "v1", first);
        Assert.Equal(proof, SecretReplayProof.Identity(fixture.Instance, connection, new("fixture-plugin", Guid.NewGuid(), "account"), "v1", first));
        Assert.NotEqual(proof, SecretReplayProof.Identity(fixture.Instance, connection, new("fixture-plugin", key.SubjectId, "another"), "v1", first));
        Assert.NotEqual(proof, SecretReplayProof.Identity(fixture.Instance, connection, key, "v2", first));
        Assert.NotEqual(proof, SecretReplayProof.Identity(fixture.Instance, connection, key, "v1", second));
        using var otherConnection = DatabaseConnections.Open(other.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        Assert.NotEqual(proof, SecretReplayProof.Identity(other.Instance, otherConnection, key, "v1", first));
        Assert.DoesNotContain("synthetic", proof, StringComparison.Ordinal); Assert.Equal(64, proof.Length);
    }
}
