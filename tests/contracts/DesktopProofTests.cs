using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class DesktopProofTests
{
    [Fact(DisplayName = "A Verified Desktop Response Preserves The Exact Signed Payload And Its Current Schema")]
    public void PreservesSignedPayload()
    {
        using var signer = new DesktopResponseSigner();
        var payload = Payload();
        var wire = JsonSerializer.SerializeToUtf8Bytes(signer.Sign(payload));
        using var verified = new DesktopProofReader(new ContractReader()).Read(wire, signer.VerificationKeyBase64, "desktop-session.schema.json");
        Assert.Equal(payload, System.Text.Encoding.UTF8.GetBytes(verified.RootElement.GetRawText()));
    }

    [Theory(DisplayName = "A Desktop Proof Cannot Be Altered Or Replaced By An Untrusted Host")]
    [InlineData("payload")]
    [InlineData("signature")]
    [InlineData("key")]
    [InlineData("algorithm")]
    [InlineData("version")]
    [InlineData("unknown")]
    public void RejectsUntrustedProofs(string mutation)
    {
        using var signer = new DesktopResponseSigner();
        using var foreign = new DesktopResponseSigner();
        var envelope = JsonSerializer.SerializeToNode(signer.Sign(Payload()))!.AsObject();
        var key = signer.VerificationKeyBase64;
        switch (mutation)
        {
            case "payload": envelope["payloadBase64"] = Convert.ToBase64String(Payload(2)); break;
            case "signature": envelope["signatureBase64"] = Convert.ToBase64String(new byte[64]); break;
            case "key": key = foreign.VerificationKeyBase64; break;
            case "algorithm": envelope["algorithm"] = "none"; break;
            case "version": envelope["version"] = 17; break;
            case "unknown": envelope["native"] = true; break;
        }
        var wire = JsonSerializer.SerializeToUtf8Bytes(envelope);
        Assert.Throws<JsonException>(() => new DesktopProofReader(new ContractReader()).Read(wire, key, "desktop-session.schema.json"));
    }

    [Fact(DisplayName = "A Valid Signature Cannot Authorize An Unknown Payload Format Or A Curve Outside The Protocol")]
    public void RejectsUnknownProtocols()
    {
        using var signer = new DesktopResponseSigner();
        var wire = JsonSerializer.SerializeToUtf8Bytes(signer.Sign(Payload(2)));
        var reader = new DesktopProofReader(new ContractReader());
        Assert.Throws<JsonException>(() => reader.Read(wire, signer.VerificationKeyBase64, "desktop-session.schema.json"));
        using var otherCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var key = Convert.ToBase64String(otherCurve.ExportSubjectPublicKeyInfo());
        Assert.Throws<JsonException>(() => reader.Read(wire, key, "desktop-session.schema.json"));
    }

    private static byte[] Payload(int version = 1) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = "nexus.desktop.session", version, instanceId = Guid.NewGuid(), clientId = Guid.NewGuid(),
        sessionId = Guid.NewGuid(), correlationId = Guid.NewGuid(), capabilities = new[] { "file.select", "folder.select", "external.open" }
    });
}
