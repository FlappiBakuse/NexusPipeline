using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Security;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ReleaseTrustTests
{
    [Fact(DisplayName = "A Frozen Release Configuration Binds Both Readers To The Same Signing Authority And Independent Channel Floors")]
    public void BindsReleaseReaders()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Configuration(signer));
        var trust = Assert.IsType<ReleaseTrustConfiguration>(ReleaseTrustConfiguration.Read(bytes));
        var original = "bound original metadata"u8.ToArray();
        var signature = signer.SignData(original, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        trust.Extensions.Policy.Verify("release-primary", original, signature);
        trust.Updates.Policy.Verify("release-primary", original, signature);
        Assert.Equal(3, trust.Extensions.Policy.Floor(ExtensionStoreChannel.Stable));
        Assert.Equal(4, trust.Extensions.Policy.Floor(ExtensionStoreChannel.Develop));
        Assert.Equal(5, trust.Updates.Policy.Floor(HostUpdateChannel.Stable));
        Assert.Equal(6, trust.Updates.Policy.Floor(HostUpdateChannel.Prerelease));
        Assert.Equal("production", trust.Updates.Policy.PayloadProfile);
        original[0] ^= 1;
        Assert.Throws<IOException>(() => trust.Extensions.Policy.Verify("release-primary", original, signature));
        Assert.Throws<IOException>(() => trust.Updates.Policy.Verify("release-primary", original, signature));
        Assert.Throws<IOException>(() => trust.Extensions.Policy.RequireArtifactUrl("https://github.com/foreign/repository/releases/download/v1/a.zip"));
        Assert.Throws<IOException>(() => trust.Updates.Policy.RequireArtifact("https://github.com/foreign/repository/releases/download/v1/a.zip"));
    }

    [Fact(DisplayName = "An Explicitly Unconfigured Build Has No Release Authority And Cannot Hide Key Material")]
    public void KeepsUnconfiguredClosed()
    {
        Assert.Null(ReleaseTrustConfiguration.Read("{\"format\":\"nexus.release.trust\",\"version\":1,\"state\":\"unconfigured\"}"u8.ToArray()));
        Assert.Throws<IOException>(() => ReleaseTrustConfiguration.Read("{\"format\":\"nexus.release.trust\",\"version\":1,\"state\":\"unconfigured\",\"keys\":{}}"u8.ToArray()));
    }

    [Theory(DisplayName = "Release Trust Rejects Fixture Keys Unapproved Origins Invalid Watermarks And Ambiguous Documents")]
    [InlineData("fixture-key")]
    [InlineData("fixture-id")]
    [InlineData("empty-keys")]
    [InlineData("curve")]
    [InlineData("test-domain")]
    [InlineData("http")]
    [InlineData("query")]
    [InlineData("floor")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("oversize")]
    public void RejectsUnsafeConfiguration(string fault)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = Configuration(signer);
        switch (fault)
        {
            case "fixture-key": root["keys"]!["release-primary"] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6uyjBJaylz5W+08fYBHMupD7vnjEOsNOFQGlLksjWMd+NAK85+0rrHkg8zoubm64PNjUvrMV7JOhQKRVUdWDBg=="; break;
            case "fixture-id": root["keys"] = new JsonObject { ["fixture-authority"] = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) }; break;
            case "empty-keys": root["keys"] = new JsonObject(); break;
            case "curve":
                using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP384)) root["keys"]!["release-primary"] = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
                break;
            case "test-domain": root["storeRoot"] = "https://store.example.test/metadata/store/1/"; break;
            case "http": root["feedRoot"] = "http://github.com/FlappiBakuse/metadata/"; break;
            case "query": root["artifactRoots"] = new JsonArray("https://github.com/FlappiBakuse/NexusPipeline/releases/download/?x=1"); break;
            case "floor": root["storeStableFloor"] = 0; break;
            case "unknown": root["privateKey"] = "not allowed"; break;
            case "version": root["version"] = 2; break;
        }
        var json = root.ToJsonString();
        if (fault == "duplicate") json = "{\"state\":\"configured\"," + json[1..];
        var original = fault == "oversize" ? new byte[16385] : System.Text.Encoding.UTF8.GetBytes(json);
        Assert.Throws<IOException>(() => ReleaseTrustConfiguration.Read(original));
    }

    private static JsonObject Configuration(ECDsa signer) => new()
    {
        ["format"] = "nexus.release.trust", ["version"] = 1, ["state"] = "configured",
        ["keys"] = new JsonObject { ["release-primary"] = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) },
        ["storeRoot"] = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/metadata/store/1/",
        ["feedRoot"] = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/metadata/update/1/",
        ["artifactRoots"] = new JsonArray("https://github.com/FlappiBakuse/NexusPipeline/releases/download/", "https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/"),
        ["storeStableFloor"] = 3, ["storeDevelopFloor"] = 4, ["updateStableFloor"] = 5, ["updatePrereleaseFloor"] = 6
    };
}
