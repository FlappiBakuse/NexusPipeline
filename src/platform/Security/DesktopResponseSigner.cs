using System.Security.Cryptography;

namespace NexusPipeline.Platform.Security;

public sealed class DesktopResponseSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly object _gate = new();
    public string VerificationKeyBase64 { get; }

    public DesktopResponseSigner() => VerificationKeyBase64 = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public object Sign(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > 12288) throw new ArgumentException("Desktop response size is outside the signed envelope limit");
        byte[] signature;
        lock (_gate) signature = _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new
        {
            format = "nexus.desktop.proof", version = 1, algorithm = "ES256",
            payloadBase64 = Convert.ToBase64String(payload), signatureBase64 = Convert.ToBase64String(signature)
        };
    }

    public void Dispose() { lock (_gate) _key.Dispose(); }
}
