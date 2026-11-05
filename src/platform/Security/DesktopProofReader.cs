using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Security;

public sealed class DesktopProofReader(ContractReader contracts)
{
    public JsonDocument Read(ReadOnlyMemory<byte> bytes, string trustedVerificationKey, string payloadSchema)
    {
        using var proof = contracts.Read(bytes, "desktop-proof.schema.json");
        try
        {
            if (trustedVerificationKey.Length is 0 or > 256) throw new JsonException("desktop_verification_key_invalid");
            using var key = ECDsa.Create();
            var publicKey = Convert.FromBase64String(trustedVerificationKey);
            key.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length || key.KeySize != 256 ||
                key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new JsonException("desktop_verification_key_invalid");
            var payload = Convert.FromBase64String(proof.RootElement.GetProperty("payloadBase64").GetString()!);
            var signature = Convert.FromBase64String(proof.RootElement.GetProperty("signatureBase64").GetString()!);
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new JsonException("desktop_response_proof_invalid");
            return contracts.Read(payload, payloadSchema);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            throw new JsonException("desktop_response_proof_invalid", exception);
        }
    }
}
