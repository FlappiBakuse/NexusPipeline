namespace NexusPipeline.Core.Extensions;

public enum ExtensionStoreChannel { Stable, Develop }
public sealed record ExtensionCatalogIdentity(ExtensionStoreChannel Channel, Guid GenerationId, long Sequence, string Sha256);
public sealed record ExtensionCatalogWatermark(long Sequence, string Sha256);

public static class CatalogSequencePolicy
{
    public const long MaximumSequence = 9007199254740991;
    public static void Require(ExtensionCatalogIdentity candidate, long releaseFloor, ExtensionCatalogWatermark? accepted)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Enum.IsDefined(candidate.Channel) || candidate.GenerationId == Guid.Empty ||
            !Sequence(candidate.Sequence) || !Sequence(releaseFloor) || !Digest(candidate.Sha256) ||
            accepted is not null && (!Sequence(accepted.Sequence) || !Digest(accepted.Sha256)))
            throw new ArgumentException("extension_catalog_identity_invalid");
        if (candidate.Sequence < releaseFloor || accepted is not null &&
            (candidate.Sequence < accepted.Sequence || candidate.Sequence == accepted.Sequence && candidate.Sha256 != accepted.Sha256))
            throw new InvalidOperationException("extension_catalog_rollback_rejected");
    }

    private static bool Sequence(long value) => value is >= 1 and <= MaximumSequence;
    private static bool Digest(string value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
