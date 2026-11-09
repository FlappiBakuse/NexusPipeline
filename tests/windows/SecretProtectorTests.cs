using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class SecretProtectorTests
{
    [Theory(DisplayName = "Actual Windows User Protection Round Trips Only Through The Bound Cipher Payload")]
    [InlineData("ascii")]
    [InlineData("unicode")]
    [InlineData("maximum")]
    public void RoundTripsCiphertext(string kind)
    {
        var text = kind switch
        {
            "ascii" => "fixture-only-token-018",
            "unicode" => "验收凭据-世界-🎮-fixture-only",
            _ => new string('测', 65536)
        };
        var protector = new UserSecretProtector();
        var instance = Guid.NewGuid();
        var secret = Guid.NewGuid();
        using var input = new SecretValue(text);
        var payload = protector.Protect(instance, secret, input);
        var second = protector.Protect(instance, secret, input);
        using var owner = WindowsIdentity.GetCurrent();
        Assert.Equal("windows-user-dpapi-utf8-v1", payload.ProtectionScheme);
        Assert.Equal(owner.User!.Value, payload.OwnerSid);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload.Ciphertext.Span)), payload.Sha256);
        Assert.False(payload.Ciphertext.Span.SequenceEqual(second.Ciphertext.Span));
        Assert.True(payload.Ciphertext.Span.IndexOf(Encoding.UTF8.GetBytes(text)) < 0);
        var before = payload.Ciphertext.ToArray();
        using var recovered = protector.Unprotect(instance, secret, payload);
        recovered.Use(value => Assert.True(value.SequenceEqual(text)));
        Assert.Equal(before, payload.Ciphertext.ToArray());
    }

    [Theory(DisplayName = "Unverified Or Unavailable Secrets Are Rejected Without Changing Their Ciphertext")]
    [InlineData("instance")]
    [InlineData("secret")]
    [InlineData("scheme")]
    [InlineData("owner")]
    [InlineData("digest")]
    [InlineData("cipher")]
    [InlineData("cipher-with-matching-digest")]
    public void PreservesUnavailablePayloads(string fault)
    {
        var protector = new UserSecretProtector();
        var instance = Guid.NewGuid();
        var secret = Guid.NewGuid();
        using var input = new SecretValue("fixture-only-token-018");
        var payload = protector.Protect(instance, secret, input);
        if (fault == "instance") instance = Guid.NewGuid();
        if (fault == "secret") secret = Guid.NewGuid();
        if (fault == "scheme") payload = payload with { ProtectionScheme = "unknown-protection-v2" };
        if (fault == "owner") payload = payload with { OwnerSid = "S-1-0-0" };
        if (fault == "digest") payload = payload with { Sha256 = new string('0', 64) };
        if (fault is "cipher" or "cipher-with-matching-digest")
        {
            var altered = payload.Ciphertext.ToArray();
            altered[altered.Length / 2] ^= 1;
            payload = payload with { Ciphertext = altered };
            if (fault == "cipher-with-matching-digest") payload = payload with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(altered)) };
        }
        var before = payload.Ciphertext.ToArray();
        var failure = Assert.Throws<IOException>(() => protector.Unprotect(instance, secret, payload));
        Assert.DoesNotContain("fixture-only-token", failure.ToString());
        Assert.Equal(before, payload.Ciphertext.ToArray());
    }

    [Theory(DisplayName = "Secret Protection Cannot Use An Empty Instance Or Secret Identity")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresSecretIdentity(bool emptyInstance)
    {
        using var input = new SecretValue("fixture-only-token");
        Assert.Throws<ArgumentException>(() => new UserSecretProtector().Protect(
            emptyInstance ? Guid.Empty : Guid.NewGuid(), emptyInstance ? Guid.NewGuid() : Guid.Empty, input));
    }
}
