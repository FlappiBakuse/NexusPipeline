using System.Collections.Immutable;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Core.Maintenance;

public enum HostUpdateChannel { Stable, Prerelease }
public sealed record UpdateFeedIdentity(HostUpdateChannel Channel, long Sequence, string Sha256);
public sealed record HostUpdateArtifact(string Url, long SizeBytes, string Sha256);
public sealed record UpdateStoreBinding(ExtensionStoreChannel Channel, Guid GenerationId, long Sequence,
    string CatalogUrl, string CatalogSha256, string SignatureUrl);
public sealed record HostUpdateRelease(string HostVersion, string MinReaderVersion, HostUpdateArtifact Artifact,
    string PayloadManifestSha256, DateTimeOffset PublishedAt, UpdateStoreBinding StoreBinding, long ReleaseId, long AssetId);
public sealed record HostUpdateFeed(UpdateFeedIdentity Identity, ImmutableArray<HostUpdateRelease> Releases);

public static class UpdateFeedPolicy
{
    public const long MaximumSequence = 9007199254740991;

    public static void Require(UpdateFeedIdentity proposed, long minimumSequence, UpdateFeedIdentity? accepted)
    {
        if (proposed is null || !Enum.IsDefined(proposed.Channel) || proposed.Sequence is < 1 or > MaximumSequence ||
            !Digest(proposed.Sha256) || minimumSequence is < 1 or > MaximumSequence)
            throw new ArgumentException("update_feed_identity_invalid");
        if (proposed.Sequence < minimumSequence) throw new InvalidOperationException("update_feed_below_release_floor");
        if (accepted is null) return;
        if (accepted.Channel != proposed.Channel || accepted.Sequence is < 1 or > MaximumSequence || !Digest(accepted.Sha256))
            throw new ArgumentException("update_feed_watermark_invalid");
        if (proposed.Sequence < accepted.Sequence) throw new InvalidOperationException("update_feed_rollback");
        if (proposed.Sequence == accepted.Sequence && proposed.Sha256 != accepted.Sha256)
            throw new InvalidOperationException("update_feed_equivocation");
    }

    private static bool Digest(string value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
