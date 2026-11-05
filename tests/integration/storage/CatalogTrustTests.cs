using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CatalogTrustTests
{
    [Theory(DisplayName = "Original Bytes Current Key And Strict Wire All Remain Required")]
    [InlineData("byte")]
    [InlineData("signed-byte")]
    [InlineData("key")]
    [InlineData("algorithm")]
    [InlineData("encoding")]
    [InlineData("der")]
    [InlineData("missing")]
    [InlineData("bom")]
    [InlineData("channel")]
    public void RequiresCurrentTrust(string fault)
    {
        using var fixture = new ExtensionCatalogFixture();
        var bytes = fixture.Signed(); _ = fixture.Verify(bytes);
        var invalid = fault switch
        {
            "signed-byte" => TamperSignedBytes(bytes),
            "byte" => bytes with { Catalog = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes.Catalog) + " ") },
            "key" => bytes with { Signature = ExtensionCatalogFixture.Edit(bytes.Signature, "keyId", JsonValue.Create("unknown-key")) },
            "algorithm" => bytes with { Signature = ExtensionCatalogFixture.Edit(bytes.Signature, "algorithm", JsonValue.Create("ecdsa-p384-sha384")) },
            "encoding" => bytes with { Signature = ExtensionCatalogFixture.Edit(bytes.Signature, "encoding", JsonValue.Create("base64-der")) },
            "der" => bytes with { Signature = ExtensionCatalogFixture.Edit(bytes.Signature, "signature", JsonValue.Create(Convert.ToBase64String(
                fixture.Signer.SignData(bytes.Catalog, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)))) },
            "missing" => bytes with { Signature = [] },
            "bom" => bytes with { Catalog = new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes.Catalog).ToArray() },
            _ => bytes
        };
        if (fault == "channel") Assert.Throws<IOException>(() => fixture.Verify(bytes, ExtensionStoreChannel.Develop));
        else Assert.Throws<IOException>(() => fixture.Verify(invalid));
        Assert.Equal(0, fixture.Count("extension_catalog_heads"));
    }

    private static SignedCatalogBytes TamperSignedBytes(SignedCatalogBytes bytes)
    {
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes.Catalog) + " ");
        return bytes with { Catalog = changed, Pointer = ExtensionCatalogFixture.Edit(bytes.Pointer,
            "catalogSha256", JsonValue.Create(Convert.ToHexStringLower(SHA256.HashData(changed)))) };
    }

    [Theory(DisplayName = "Valid Signatures Cannot Authorize Duplicate Identity Or An Unapproved Source")]
    [InlineData("duplicate-id")]
    [InlineData("artifact-case")]
    [InlineData("foreign-url")]
    [InlineData("credentials")]
    [InlineData("unsupported-host")]
    public void RejectsUnapprovedSources(string fault)
    {
        using var fixture = new ExtensionCatalogFixture();
        var bytes = fixture.Signed(edit: catalog =>
        {
            var items = catalog["entries"]!.AsArray(); var first = items[0]!.AsObject();
            if (fault is "duplicate-id" or "artifact-case")
            {
                var other = first.DeepClone().AsObject();
                if (fault == "artifact-case") { other["extensionId"] = "other-plugin"; other["artifactName"] = "fixtureplugin"; }
                items.Add(other);
            }
            else if (fault == "unsupported-host") first["minHostVersion"] = "99.0.0";
            else first["artifact"]!["url"] = fault == "credentials"
                ? "https://user:password@assets.example.test/releases/download/a.zip"
                : "https://other.example.test/releases/download/a.zip";
        });
        Assert.Throws<IOException>(() => fixture.Verify(bytes));
        Assert.Equal(0, fixture.Count("operations"));
    }

    [Fact(DisplayName = "Verification Freezes The Caller Buffers And The Configured Public Key")]
    public void FreezesTrustInputs()
    {
        using var fixture = new ExtensionCatalogFixture();
        var bytes = fixture.Signed();
        var spki = fixture.Signer.ExportSubjectPublicKeyInfo(); var verifier = fixture.NewVerifier(spki);
        Array.Fill(spki, (byte)0);
        var verified = verifier.Verify(bytes.Pointer, bytes.Catalog, bytes.Signature, ExtensionStoreChannel.Stable);
        Array.Fill(bytes.Pointer, (byte)0); Array.Fill(bytes.Catalog, (byte)0); Array.Fill(bytes.Signature, (byte)0);
        var returned = verified.CatalogBytes(); Array.Fill(returned, (byte)0);
        Assert.Equal(verified.Identity, fixture.Store.Accept(Guid.NewGuid(), "fixture", verified).Identity);
        Assert.Equal(verified.Identity, fixture.Store.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        Assert.Equal("fixture-plugin", Assert.Single(verified.Entries).ExtensionId);
    }
}
