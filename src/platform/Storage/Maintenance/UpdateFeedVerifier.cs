using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class UpdateFeedVerifier(ContractReader contracts, UpdateTrustPolicy trust)
{
    internal VerifiedUpdateFeed Verify(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> signature, HostUpdateChannel channel)
    {
        try { return VerifiedUpdateFeed.Verify(this, original, signature, channel); }
        catch (Exception error) when (error is JsonException or CryptographicException or FormatException or ArgumentException or InvalidOperationException)
        { throw new IOException("update_feed_signature_invalid", error); }
    }

    internal T VerifyCore<T>(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> signature, HostUpdateChannel channel,
        Func<HostUpdateFeed, string, byte[], byte[], T> accept)
    {
        var bytes = Own(original); var detachedBytes = Own(signature);
        using var detached = contracts.Read(detachedBytes, "detached-signature.schema.json");
        var signed = detached.RootElement;
        if (signed.GetProperty("algorithm").GetString() != "ecdsa-p256-sha256" ||
            signed.GetProperty("encoding").GetString() != "base64-p1363") throw Invalid();
        var encoded = signed.GetProperty("signature").GetString()!;
        var proof = Convert.FromBase64String(encoded);
        if (proof.Length != 64 || Convert.ToBase64String(proof) != encoded) throw Invalid();
        var key = signed.GetProperty("keyId").GetString()!;
        trust.Verify(key, bytes, proof);
        var feed = new UpdateFeedCodec(contracts).Read(bytes, channel);
        UpdateFeedPolicy.Require(feed.Identity, trust.Floor(channel), null);
        foreach (var release in feed.Releases)
        {
            trust.RequireArtifact(release.Artifact.Url);
            trust.RequireStore(release.StoreBinding);
        }
        return accept(feed, key, bytes, detachedBytes);
    }

    internal long Floor(HostUpdateChannel channel) => trust.Floor(channel);
    internal Version ReaderVersion => trust.ReaderVersion;
    internal string PayloadProfile => trust.PayloadProfile;
    private static byte[] Own(ReadOnlyMemory<byte> input)
    {
        if (input.Length is < 1 or > StrictJson.MaximumBytes || input.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw Invalid();
        return input.ToArray();
    }
    private static IOException Invalid() => new("update_feed_signature_invalid");
}
