using System.Text.Json;
using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class HostKeyTests
{
    [Fact(DisplayName = "A Host Proxy Field Has An Explicit Owner And Round Trips Without An Extension Identity")]
    public void BindsHostOwner()
    {
        var instance = Guid.NewGuid();
        var key = SecretFieldKey.HostProxyPassword(instance);
        Assert.Equal(FieldOwnerKind.Host, key.OwnerKind);
        Assert.Null(key.ExtensionId);
        Assert.Equal(instance, key.SubjectId);
        Assert.Equal("proxy-password", key.Field);
        Assert.Equal(key, JsonSerializer.Deserialize<SecretFieldKey>(JsonSerializer.Serialize(key)));
    }

    [Fact(DisplayName = "An Extension Named Host Cannot Alias The Fixed Host Proxy Field Or Authorize It")]
    public void RejectsExtensionAliases()
    {
        var subject = Guid.NewGuid();
        var host = SecretFieldKey.HostProxyPassword(subject);
        var extension = new SecretFieldKey("host", subject, "proxy-password");
        Assert.NotEqual(host, extension);
        Assert.Equal(FieldOwnerKind.Extension, extension.OwnerKind);
        extension.RequireExtensionOwner("host");
        Assert.Throws<UnauthorizedAccessException>(() => host.RequireExtensionOwner("host"));
        Assert.Throws<UnauthorizedAccessException>(() => extension.RequireExtensionOwner("another"));
    }

    [Theory(DisplayName = "Unknown Owner Kinds Cannot Become An Extension Or Host Field")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void RejectsUnknownOwners(int owner)
    {
        Assert.Throws<ArgumentException>(() => new SecretFieldKey((FieldOwnerKind)owner, null, Guid.NewGuid(), "proxy-password"));
    }

    [Theory(DisplayName = "Host Does Not Accept An Arbitrary Field Or Any Extension Identity")]
    [InlineData("arbitrary", null)]
    [InlineData("proxy-password", "host")]
    [InlineData("proxy-password", "fixture-extension")]
    [InlineData("proxy-password", "")]
    public void RejectsForeignFields(string field, string? extension)
    {
        Assert.Throws<ArgumentException>(() => new SecretFieldKey(FieldOwnerKind.Host, extension, Guid.NewGuid(), field));
    }

    [Fact(DisplayName = "A Host Field Requires The Actual Nonempty Instance Identity")]
    public void RequiresInstanceIdentity()
    {
        Assert.Throws<ArgumentException>(() => SecretFieldKey.HostProxyPassword(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SecretFieldKey("", Guid.NewGuid(), "proxy-password"));
    }

    [Theory(DisplayName = "Missing Or Unknown Json Owner Does Not Default To Extension")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMissingOwners(bool unknown)
    {
        var owner = unknown ? "\"OwnerKind\":87," : "";
        var json = "{" + owner + "\"ExtensionId\":\"fixture-extension\",\"SubjectId\":\"" +
            Guid.NewGuid().ToString("D") + "\",\"Field\":\"credential\"}";
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<SecretFieldKey>(json));
    }
}
