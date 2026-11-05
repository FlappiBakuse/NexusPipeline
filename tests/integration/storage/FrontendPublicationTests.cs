using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Secrets;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class FrontendPublicationTests
{
    [Theory(DisplayName = "A Prior Browser Grant Uses The Current Request Authority Across An Actual Atomic Secret Publication")]
    [InlineData(SecretDisposition.Set)]
    [InlineData(SecretDisposition.Keep)]
    [InlineData(SecretDisposition.Clear)]
    public async Task PublishesCurrentRequest(SecretDisposition disposition)
    {
        using var fixture = new ExtensionCatalogFixture();
        var secrets = new SecretFieldService(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new());
        const string token = "synthetic_Frontend_Publication_Bearer_032";
        using var bearer = new SecretValue(token);
        await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId), 0, SecretDisposition.Set, bearer, default);
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, secrets);
        var creator = Caller(verifier.Verify(token)!);
        using var clients = new FrontendClients(TimeProvider.System);
        var grant = clients.Create(creator, "http://fixture");
        var proof = verifier.Verify(token)!; var caller = Caller(proof);
        using var request = proof.EnterRequest();
        var identity = clients.Resolve(grant.Token, caller, "http://fixture");
        var service = new SecretDocumentService(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new(), secretService: secrets);
        using var value = disposition == SecretDisposition.Set ? new SecretValue("synthetic-private-value") : null;
        var result = await service.WriteAsync(Guid.NewGuid(), caller.SubjectId, "fixture-plugin", "tasks", 0, "{}"u8.ToArray(),
            [new(Guid.NewGuid(), "credential", 0, disposition, value)], identity.RequireCurrent, default);
        Assert.Equal(1, result.Revision); Assert.True(fixture.Index.Current.IsReady);
        identity.RequireCurrent(); Assert.False(identity.Lifetime.IsCancellationRequested);
        Assert.NotNull(verifier.Verify(token));
    }

    private static AuthenticatedControlRequest Caller(VerifiedControlBearer proof) =>
        new(ControlOrigin.Remote, "remote:fixture", proof, () => new(false, false));

    [Theory(DisplayName = "A Browser Grant Cannot Transfer To A Rotated Credential Another Verifier Or An Unbound Request")]
    [InlineData("rotation")]
    [InlineData("instance")]
    [InlineData("unbound")]
    public async Task RejectsChangedCredential(string changed)
    {
        using var fixture = new ExtensionCatalogFixture(); using var other = new ExtensionCatalogFixture();
        var secrets = new SecretFieldService(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new());
        var otherSecrets = new SecretFieldService(other.Instance, other.Database, other.Lease, other.Resources, other.Index, new());
        const string token = "synthetic_Frontend_Grant_Original_Bearer_032";
        using var original = new SecretValue(token);
        await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId), 0, SecretDisposition.Set, original, default);
        await otherSecrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostControlBearer(other.Instance.InstanceId), 0, SecretDisposition.Set, original, default);
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, secrets);
        using var otherVerifier = new ControlBearerVerifier(other.Instance, other.Database, other.Lease, other.Index, otherSecrets);
        using var clients = new FrontendClients(TimeProvider.System);
        var grant = clients.Create(Caller(verifier.Verify(token)!), "http://fixture");
        AuthenticatedControlRequest caller;
        if (changed == "rotation")
        {
            const string next = "synthetic_Frontend_Grant_Replacement_Bearer_032";
            using var replacement = new SecretValue(next);
            await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId), 1, SecretDisposition.Set, replacement, default);
            caller = Caller(verifier.Verify(next)!);
        }
        else caller = changed == "instance" ? Caller(otherVerifier.Verify(token)!) : Caller(null!);
        Assert.Throws<UnauthorizedAccessException>(() => clients.Resolve(grant.Token, caller, "http://fixture"));
        Assert.Throws<UnauthorizedAccessException>(() => clients.Resolve(grant.Token, Caller(verifier.Verify(changed == "rotation" ?
            "synthetic_Frontend_Grant_Replacement_Bearer_032" : token)!), "http://fixture"));
    }
}
