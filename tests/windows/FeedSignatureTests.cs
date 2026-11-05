using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Tests.Fixtures;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class FeedSignatureTests
{
    [Theory(DisplayName = "Update Feed Original Bytes And Current Trust Bind The Independent Update Domain")]
    [InlineData("bytes")]
    [InlineData("current-key")]
    [InlineData("bom")]
    [InlineData("channel")]
    [InlineData("store-binding")]
    [InlineData("artifact-root")]
    public void BindsFeedTrust(string fault)
    {
        using var fixture = new UpdateFeedFixture();
        var document = fixture.Document();
        if (fault == "store-binding") document["releases"]![0]!["storeBinding"]!["catalogUrl"] = "https://foreign.example.test/catalog.json";
        if (fault == "artifact-root") document["releases"]![0]!["artifact"]!["url"] = "https://foreign.example.test/Host.zip";
        var bytes = fixture.Signed(document);
        var verifier = fixture.Verifier();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (fault == "bytes") bytes.Feed[^1] ^= 1;
        if (fault == "current-key") verifier = fixture.Verifier(other.ExportSubjectPublicKeyInfo());
        if (fault == "bom") { bytes.Feed = new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes.Feed).ToArray(); bytes.Signature = fixture.Signature(bytes.Feed); }
        var channel = fault == "channel" ? HostUpdateChannel.Prerelease : HostUpdateChannel.Stable;
        Assert.ThrowsAny<IOException>(() => verifier.Verify(bytes.Feed, bytes.Signature, channel));
    }

    [Fact(DisplayName = "An Update Feed Freezes Its Caller Bytes And Public Key Before Later Mutation")]
    public void FreezesFeedBytes()
    {
        using var fixture = new UpdateFeedFixture(); var key = fixture.Signer.ExportSubjectPublicKeyInfo();
        var verifier = fixture.Verifier(key); var bytes = fixture.Signed(fixture.Document());
        var expected = bytes.Feed.ToArray(); var valid = verifier.Verify(bytes.Feed, bytes.Signature, HostUpdateChannel.Stable);
        bytes.Feed.AsSpan().Fill(0); bytes.Signature.AsSpan().Fill(0); key.AsSpan().Fill(0);
        Assert.Equal(expected, valid.FeedBytes());
        var owned = valid.FeedBytes(); owned.AsSpan().Fill(0);
        Assert.Equal(expected, valid.FeedBytes());
        Assert.Equal(valid.Feed.Identity, verifier.Verify(valid.FeedBytes(), valid.SignatureBytes(), HostUpdateChannel.Stable).Feed.Identity);
        Assert.Contains("nexus.update.feed", Encoding.UTF8.GetString(expected), StringComparison.Ordinal);
    }
}
