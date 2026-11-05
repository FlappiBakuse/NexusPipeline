using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class VerifiedUpdateFeed
{
    private readonly byte[] _feed, _signature;
    internal HostUpdateFeed Feed { get; }
    internal string KeyId { get; }

    private VerifiedUpdateFeed(HostUpdateFeed feed, string keyId, byte[] original, byte[] signature)
    { Feed = feed; KeyId = keyId; _feed = original.ToArray(); _signature = signature.ToArray(); }

    internal static VerifiedUpdateFeed Verify(UpdateFeedVerifier verifier, ReadOnlyMemory<byte> original,
        ReadOnlyMemory<byte> signature, HostUpdateChannel channel) =>
        verifier.VerifyCore(original, signature, channel, (feed, key, f, s) => new VerifiedUpdateFeed(feed, key, f, s));

    internal byte[] FeedBytes() => _feed.ToArray();
    internal byte[] SignatureBytes() => _signature.ToArray();
}
